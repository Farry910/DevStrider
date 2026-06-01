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
    private const string ExtensionSource = "Extension";

    private readonly BidBoardService _bids;
    private readonly SettingsService _settingsService;
    private readonly ActivityLogService _activity;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _boundPort;
    [ObservableProperty] private string _status = "Stopped";

    /// <summary>
    /// Fires after a successful <c>/record-bid</c> request. The Bid Board VM subscribes so
    /// the new row appears without the user having to hit refresh. Raised on a thread-pool
    /// thread — subscribers must marshal back to the UI thread themselves.
    /// </summary>
    public event Action? OnExtensionBidRecorded;

    public LocalApiServer(BidBoardService bids, SettingsService settingsService, ActivityLogService activity)
    {
        _bids = bids;
        _settingsService = settingsService;
        _activity = activity;
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

            if (ctx.Request.HttpMethod == "POST" && path == "/trigger-paste-submit")
            {
                HandleTriggerPasteSubmit(ctx);
                _activity.Info(ExtensionSource, "JD pasted into ChatGPT", silent: true);
                await WriteJsonAsync(ctx, 200, new { success = true });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/refresh-word")
            {
                await HandleRefreshWordAsync(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && path == "/browse-word")
            {
                var picked = ShowWordPickerOnUiThread();
                if (!string.IsNullOrEmpty(picked))
                {
                    _activity.Info(ExtensionSource, "Word document selected", picked, silent: true);
                    await WriteJsonAsync(ctx, 200, new { success = true, path = picked });
                }
                else
                {
                    await WriteJsonAsync(ctx, 200, new { success = false, path = (string?)null });
                }
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/client/devstrider-outcome")
            {
                // Telemetry sink — just consume and ack. The extension's purple-button flow
                // fires this regardless of success/failure; we don't store it (the WPF UI is
                // the dashboard now).
                using var sink = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                _ = await sink.ReadToEndAsync();
                await WriteJsonAsync(ctx, 200, new { ok = true });
                return;
            }

            _activity.Warning(ExtensionSource, "Unknown endpoint", $"{ctx.Request.HttpMethod} {path}", silent: true);
            await WriteJsonAsync(ctx, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            _activity.Error(ExtensionSource, "Server error", ex.Message);
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
            _activity.Error(ExtensionSource, "Bid record failed", $"Malformed JSON: {ex.Message}");
            await WriteJsonAsync(ctx, 400, new { error = $"Invalid JSON: {ex.Message}" });
            return;
        }

        if (req == null || string.IsNullOrWhiteSpace(req.Url))
        {
            _activity.Error(ExtensionSource, "Bid record failed", "Missing url in request body.");
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

        var label = parsed != null
            ? $"{parsed.Company} · {parsed.Role}".Trim(' ', '·')
            : (bid.Company?.Trim() ?? "");
        _activity.Success(ExtensionSource,
            joinedExistingLink ? "Bid updated" : "Bid recorded",
            string.IsNullOrEmpty(label) ? req.Url : label);

        // Nudge the Bid Board to reload so the new/updated row appears without manual refresh.
        try { OnExtensionBidRecorded?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LocalApi] subscriber threw: " + ex.Message); }

        await WriteJsonAsync(ctx, joinedExistingLink ? 200 : 201, new
        {
            link = new { id = link.Id.ToString(), link.Url },
            bid = new { id = bid.Id.ToString(), bid.Status, bid.ResumeId, bid.Company, bid.Role },
            joinedExistingLink,
            fastFeedApplied = parsed != null
        });
    }

    /// <summary>
    /// Sends Ctrl+V then Enter to whatever the OS has focused right now. The extension calls
    /// this AFTER setting focus to the ChatGPT input — we don't touch focus ourselves.
    /// </summary>
    private static void HandleTriggerPasteSubmit(HttpListenerContext _)
    {
        try { KeyboardHelper.PasteSubmit(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[paste-submit] {ex.Message}"); }
    }

    /// <summary>
    /// Open the user's Word doc, send the configured hotkey to trigger its macro, wait for
    /// Word to close (the macro's last action), restore focus to Chrome. Path + hotkey are
    /// read from <see cref="AppSettings"/> — the extension just POSTs an empty trigger.
    /// </summary>
    private async Task HandleRefreshWordAsync(HttpListenerContext ctx)
    {
        // Drain the request body even though we don't read it — leaving it unread can wedge
        // some HttpListener clients waiting for the response.
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            await reader.ReadToEndAsync();

        var s = await _settingsService.GetAsync();
        var wordPath = (s.WordDocPath ?? "").Trim();
        var wordHotkey = string.IsNullOrWhiteSpace(s.WordHotkey) ? "F9" : s.WordHotkey.Trim();

        if (string.IsNullOrWhiteSpace(wordPath))
        {
            _activity.Warning(ExtensionSource, "Refresh Word failed", "Set the Word document path in Settings first.");
            await WriteJsonAsync(ctx, 400, new { success = false, error = "Set the Word document path in DevStrider · Settings first." });
            return;
        }

        var (valid, pathError) = PathValidator.ValidateWordPath(wordPath);
        if (!valid)
        {
            _activity.Error(ExtensionSource, "Refresh Word failed", pathError ?? "Invalid Word document path.");
            await WriteJsonAsync(ctx, 400, new { success = false, error = pathError });
            return;
        }
        var parsed = KeyboardHelper.ParseHotkey(wordHotkey);
        if (parsed == null)
        {
            _activity.Error(ExtensionSource, "Refresh Word failed", $"Invalid hotkey: {wordHotkey}");
            await WriteJsonAsync(ctx, 400, new { success = false, error = $"Invalid hotkey: {wordHotkey}" });
            return;
        }

        try
        {
            var (mods, keyVk) = parsed.Value;
            var chromeHwnd = KeyboardHelper.GetForegroundWindow();
            var wordHwnd = KeyboardHelper.OpenWordDocument(wordPath);
            if (wordHwnd == IntPtr.Zero)
            {
                _activity.Error(ExtensionSource, "Refresh Word failed", "Couldn't open the Word document. Check the path and that Word is installed.");
                await WriteJsonAsync(ctx, 500, new { success = false, error = "Failed to open Word. Ensure Microsoft Word is installed and the path is correct." });
                return;
            }
            Thread.Sleep(KeyboardHelper.RefreshWordOpenDelayMs);

            // Fast path: no modifiers → PostMessage straight to the window without focus juggling.
            if (mods.Count == 0 && KeyboardHelper.PostSingleKeyToWindow(wordHwnd, mods, keyVk))
            {
                if (KeyboardHelper.WaitForWordClose(5))
                {
                    KeyboardHelper.ReturnToChrome(chromeHwnd);
                    _activity.Success(ExtensionSource, "Word document refreshed", System.IO.Path.GetFileName(wordPath));
                    await WriteJsonAsync(ctx, 200, new { success = true, message = "Word document refreshed" });
                    return;
                }
            }

            // Slow path: bring Word to foreground, send the hotkey (with modifiers) via SendInput.
            wordHwnd = KeyboardHelper.FindWordWindow();
            if (wordHwnd == IntPtr.Zero)
            {
                KeyboardHelper.ReturnToChrome(chromeHwnd);
                _activity.Success(ExtensionSource, "Word document refreshed", System.IO.Path.GetFileName(wordPath));
                await WriteJsonAsync(ctx, 200, new { success = true, message = "Word document refreshed" });
                return;
            }
            if (KeyboardHelper.SetForegroundWindow(wordHwnd) || KeyboardHelper.AltTabToWindow(wordHwnd))
            {
                Thread.Sleep(KeyboardHelper.HOTKEY_DELAY_MS);
                KeyboardHelper.PressHotkey(mods, keyVk);
                if (KeyboardHelper.WaitForWordClose(KeyboardHelper.WORD_CLOSE_TIMEOUT_SECONDS))
                {
                    KeyboardHelper.ReturnToChrome(chromeHwnd);
                    _activity.Success(ExtensionSource, "Word document refreshed", System.IO.Path.GetFileName(wordPath));
                    await WriteJsonAsync(ctx, 200, new { success = true, message = "Word document refreshed" });
                    return;
                }
            }
            KeyboardHelper.ReturnToChrome(chromeHwnd);
            _activity.Warning(ExtensionSource, "Word didn't close", "Hotkey was sent but Word stayed open — the macro may not have run.");
            await WriteJsonAsync(ctx, 200, new { success = false, error = "Hotkey sent, but Word didn't close. The macro may not have executed." });
        }
        catch (Exception ex)
        {
            _activity.Error(ExtensionSource, "Refresh Word crashed", ex.Message);
            await WriteJsonAsync(ctx, 500, new { success = false, error = $"Word refresh failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Marshal to the WPF UI thread and show <see cref="Microsoft.Win32.OpenFileDialog"/>. The
    /// dialog's owner is the main window if available, which keeps it above Chrome — no need
    /// for BAA's hidden-topmost-form trick.
    /// </summary>
    private static string? ShowWordPickerOnUiThread()
    {
        string? selected = null;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Word document",
                Filter = "Word macro-enabled (*.docm)|*.docm|Word documents (*.docx)|*.docx|Word 97-2003 (*.doc)|*.doc|All files (*.*)|*.*",
                FilterIndex = 1,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            var owner = System.Windows.Application.Current.MainWindow;
            if (dlg.ShowDialog(owner) == true)
            {
                var (ok, _) = PathValidator.ValidateWordPath(dlg.FileName);
                if (ok) selected = dlg.FileName;
            }
        });
        return selected;
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
