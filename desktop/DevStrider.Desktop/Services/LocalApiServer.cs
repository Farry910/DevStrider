using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Tiny HTTP listener that exposes a localhost endpoint the Bid-Assistant Chrome extension
/// can POST to. Mirrors the old web-app route
/// <c>POST /api/integrations/bid-assistant/record-bid</c> minus <c>groupId</c> and minus the
/// JWT requirement (loopback-only binding makes authentication unnecessary).
///
/// Lifecycle: created once in <c>App.OnStartup</c>, <see cref="Start"/> called at boot if
/// <see cref="AppSettings.ListenerEnabled"/> is on, stopped via <see cref="StopAsync"/> in
/// <c>App.OnExit</c>. The listener uses <see cref="HttpListener"/> (built into .NET, zero
/// extra dependencies) and processes requests on the thread pool.
/// </summary>
public sealed partial class LocalApiServer : ObservableObject
{
    private readonly BidBoardService _bids;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _boundPort;
    [ObservableProperty] private string _status = "Stopped";

    public LocalApiServer(BidBoardService bids)
    {
        _bids = bids;
    }

    public void Start(int port)
    {
        if (IsRunning) return;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BoundPort = port;
            IsRunning = true;
            Status = $"Listening on http://127.0.0.1:{port}";
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Status = $"Failed to start on :{port} — {ex.Message}";
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        if (_loop != null)
        {
            try { await _loop; } catch { /* expected on shutdown */ }
        }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Status = "Stopped";
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            // Each request fans out to the thread pool so a slow Mongo insert doesn't block the
            // listener from accepting the next call.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // CORS: extension content-scripts hit us from arbitrary origins. Loopback-only,
            // no credentials, so wildcard is fine.
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            var path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(path)) path = "/";

            if (ctx.Request.HttpMethod == "GET" && (path == "/health" || path == "/"))
            {
                await WriteJsonAsync(ctx, 200, new { ok = true, port = BoundPort });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" &&
                (path == "/record-bid" || path == "/record-devstrider"))
            {
                await HandleRecordBidAsync(ctx);
                return;
            }

            await WriteJsonAsync(ctx, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }); }
            catch { /* response may already be closed */ }
        }
    }

    private async Task HandleRecordBidAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        RecordBidRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RecordBidRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = $"Invalid JSON: {ex.Message}" });
            return;
        }

        if (req == null || string.IsNullOrWhiteSpace(req.Url))
        {
            await WriteJsonAsync(ctx, 400, new { error = "url is required" });
            return;
        }

        // Fast-feed: prefer the explicit field; otherwise lift it off the last parsing line
        // of gptResumeContent (matching the legacy server). If found, the bid jumps to
        // 'applied' and resumeId/company/role/stacks are filled.
        FastFeed.Parsed? parsed = null;
        var gptStored = req.GptResumeContent ?? "";
        if (!string.IsNullOrWhiteSpace(req.FastFeedInput))
        {
            parsed = FastFeed.ParseLine(req.FastFeedInput);
        }
        if (parsed == null && !string.IsNullOrEmpty(gptStored))
        {
            var split = FastFeed.SplitTrailing(gptStored);
            if (split.Parsed != null)
            {
                parsed = split.Parsed;
                gptStored = split.ResumePart;
            }
        }

        // Find-or-create the link by exact normalized URL (strict matching — different queries
        // = different links, per the rule the user locked earlier).
        var existing = await _bids.FindLinkByNormalizedUrlAsync(req.Url);
        GroupLink link = existing ?? await _bids.AddLinkAsync(req.Url, req.SharedJobDescription ?? "");
        bool joinedExistingLink = existing != null;

        var bid = await _bids.UpsertBidAsync(link.Id, b =>
        {
            if (!string.IsNullOrEmpty(req.JobDescription)) b.JobDescription = req.JobDescription;
            if (!string.IsNullOrEmpty(gptStored)) b.GptResumeContent = gptStored;
            if (!string.IsNullOrEmpty(req.Comment)) b.Comment = req.Comment;
            b.Origin = string.IsNullOrWhiteSpace(req.Origin) ? "Bid Assistant" : req.Origin!.Trim();
            if (parsed != null)
            {
                b.ResumeId = parsed.ResumeId;
                b.Company = parsed.Company;
                b.Role = parsed.Role;
                b.PrimaryStacks = parsed.PrimaryStacks.ToList();
                b.Status = BidStatuses.Applied;
            }
        });

        await WriteJsonAsync(ctx, joinedExistingLink ? 200 : 201, new
        {
            link = new { id = link.Id.ToString(), link.Url },
            bid = new { id = bid.Id.ToString(), bid.Status, bid.ResumeId, bid.Company, bid.Role },
            joinedExistingLink,
            fastFeedApplied = parsed != null
        });
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.LongLength;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>Wire shape mirroring the legacy <c>/api/integrations/bid-assistant/record-bid</c> body.</summary>
    public class RecordBidRequest
    {
        public string Url { get; set; } = "";
        public string? JobDescription { get; set; }
        public string? GptResumeContent { get; set; }
        public string? FastFeedInput { get; set; }
        public string? SharedJobDescription { get; set; }
        public string? Comment { get; set; }
        public string? Origin { get; set; }
    }
}
