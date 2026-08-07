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
    private readonly ProfileContext _profileContext;
    private readonly ResumeQueueService _resumeQueue;
    private readonly WordMacroService _wordMacro;

    /// <summary>
    /// Serializes <see cref="HandleRefreshWordAsync"/> end to end. Opening an already-open
    /// .docm just reactivates the same Word window, so two Chrome windows triggering a refresh
    /// at once would otherwise retrigger the same macro mid-run and stomp each other's edits.
    /// </summary>
    private readonly SemaphoreSlim _refreshWordLock = new(1, 1);

    /// <summary>Requests currently holding or waiting on <see cref="_refreshWordLock"/>.</summary>
    private int _refreshWordQueueDepth;

    /// <summary>
    /// Refresh timing budget. The extension abandons <c>/refresh-word</c> at 90s, so queue-wait
    /// plus automation must stay under that or callers time out on a request the app still
    /// thinks is live. 40 + 45 = 85s leaves a small margin.
    /// </summary>
    private static readonly TimeSpan RefreshWordQueueTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RefreshWordAutomationTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Past this many stacked-up refreshes the queue is doing more harm than good — the ones at
    /// the back would time out client-side anyway, so reject immediately with a message the user
    /// can act on instead of letting them wait out the full budget for nothing.
    /// </summary>
    private const int MaxRefreshWordQueueDepth = 8;

    /// <summary>
    /// Ceiling on a single request body. Resume text and JDs are the big ones and land well
    /// under this; the cap exists so a runaway or malformed caller can't make the app buffer
    /// unbounded input into memory.
    /// </summary>
    private const long MaxRequestBodyBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Consecutive <see cref="HttpListener.GetContextAsync"/> failures tolerated before the
    /// accept loop gives up. Individual failures are usually one bad client connection, not a
    /// dead listener, so retrying beats tearing down the endpoint.
    /// </summary>
    private const int MaxConsecutiveAcceptFailures = 10;

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

    public LocalApiServer(
        BidBoardService bids,
        SettingsService settingsService,
        ActivityLogService activity,
        ProfileContext profileContext,
        ResumeQueueService resumeQueue,
        WordMacroService wordMacro)
    {
        _bids = bids;
        _settingsService = settingsService;
        _activity = activity;
        _profileContext = profileContext;
        _resumeQueue = resumeQueue;
        _wordMacro = wordMacro;
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

    /// <summary>
    /// Accept loop. A single failed <c>GetContextAsync</c> used to <c>break</c> out of here
    /// permanently while <see cref="IsRunning"/> stayed <c>true</c> and <see cref="Status"/>
    /// still read "Listening" — the app looked healthy but silently answered nothing until it
    /// was restarted. Transient failures are now retried with a short backoff, and the two ways
    /// the loop can genuinely end (shutdown, or a listener that is really gone) both leave the
    /// observable state telling the truth.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
                consecutiveFailures = 0;
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                // Listener torn down under us (Stop/Dispose, port revoked) — nothing to retry.
                if (_listener is not { IsListening: true })
                {
                    MarkListenerDown($"Listener stopped unexpectedly — {ex.Message}");
                    break;
                }

                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveAcceptFailures)
                {
                    MarkListenerDown($"Listener gave up after {consecutiveFailures} consecutive accept failures — {ex.Message}");
                    break;
                }

                _activity.Warning(ExtensionSource, "Listener accept failed",
                    $"{ex.Message} (attempt {consecutiveFailures}/{MaxConsecutiveAcceptFailures}, retrying)", silent: true);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * consecutiveFailures), ct);
                }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Each request fans out to the thread pool so a slow Mongo insert doesn't block the
            // listener from accepting the next call.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    /// <summary>
    /// Reflect a listener that died on its own (as opposed to <see cref="StopAsync"/>) into the
    /// observable state, so the tray/Settings surface shows it and the user can restart it.
    /// </summary>
    private void MarkListenerDown(string reason)
    {
        IsRunning = false;
        Status = reason;
        _activity.Error(ExtensionSource, "Listener stopped", reason);
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

            // ----- Resume auto-generation queue (extension polls these) -----
            if (ctx.Request.HttpMethod == "GET" && path == "/resume/next-job")
            {
                await HandleResumeNextJobAsync(ctx);
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/resume/result")
            {
                await HandleResumeResultAsync(ctx);
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/resume/fail")
            {
                await HandleResumeFailAsync(ctx);
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
                _ = await ReadBodyAsync(ctx);
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
        finally
        {
            // Every path above is expected to have closed the response already; this is the
            // backstop for the ones that threw before doing so. Without it the connection stays
            // open and the extension waits out its full timeout on a request that is already
            // dead server-side.
            try { ctx.Response.Close(); }
            catch { /* already closed — the normal case */ }
        }
    }

    /// <summary>
    /// Read a request body with a hard ceiling (<see cref="MaxRequestBodyBytes"/>). Returns
    /// <c>null</c> when the cap is exceeded, in which case the caller should 413 and stop.
    /// The declared <c>Content-Length</c> is only a hint — chunked requests report -1 — so the
    /// limit is enforced while reading rather than trusted up front.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxRequestBodyBytes) return null;

        var encoding = ctx.Request.ContentEncoding ?? Encoding.UTF8;
        var buffer = new byte[81920];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await ctx.Request.InputStream.ReadAsync(buffer)) > 0)
        {
            if (accumulated.Length + read > MaxRequestBodyBytes) return null;
            accumulated.Write(buffer, 0, read);
        }
        return encoding.GetString(accumulated.ToArray());
    }

    /// <summary>Standard 413 for a body that blew past <see cref="MaxRequestBodyBytes"/>.</summary>
    private async Task WriteBodyTooLargeAsync(HttpListenerContext ctx)
    {
        var limitMb = MaxRequestBodyBytes / (1024 * 1024);
        _activity.Warning(ExtensionSource, "Request rejected", $"Body exceeded {limitMb} MB.", silent: true);
        await WriteJsonAsync(ctx, 413, new { success = false, error = $"Request body exceeds {limitMb} MB." });
    }

    private async Task HandleRecordBidAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx);
        if (body == null) { await WriteBodyTooLargeAsync(ctx); return; }
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

    // ========================================================================
    // RESUME QUEUE ENDPOINTS
    // ========================================================================

    /// <summary>
    /// Hand the extension the next queued job for the active profile, plus that profile's
    /// resume prompt. 204 when the batch is stopped or nothing's queued. The job flips to
    /// Generating so it isn't handed out twice.
    /// </summary>
    private async Task HandleResumeNextJobAsync(HttpListenerContext ctx)
    {
        var job = await _resumeQueue.ClaimNextAsync();
        if (job == null)
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }
        var prompt = (_profileContext.Current?.ResumePrompt ?? "").Trim();
        _activity.Info(ExtensionSource, "Resume job dispatched", job.Url, silent: true);
        await WriteJsonAsync(ctx, 200, new
        {
            jobId = job.Id.ToString(),
            url = job.Url,
            prompt
        });
    }

    /// <summary>
    /// The extension delivers the ChatGPT output for a job. We split off the trailing
    /// fast-feed line (UID, Company, Role, stacks), run the profile's Word macro on the
    /// resume body, auto-record the bid, and mark the job Done.
    /// </summary>
    private async Task HandleResumeResultAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx);
        if (body == null) { await WriteBodyTooLargeAsync(ctx); return; }
        ResumeResultRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<ResumeResultRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = $"Invalid JSON: {ex.Message}" });
            return;
        }
        if (req == null || string.IsNullOrWhiteSpace(req.JobId) || !ObjectId.TryParse(req.JobId, out var jobId))
        {
            await WriteJsonAsync(ctx, 400, new { error = "jobId is required" });
            return;
        }

        var job = await _resumeQueue.GetAsync(jobId);
        if (job == null)
        {
            await WriteJsonAsync(ctx, 404, new { error = "Unknown job" });
            return;
        }

        await _resumeQueue.SetStatusAsync(jobId, ResumeJobStatuses.ResumeReceived);

        // 1) Split the trailing "UID, Company, Role, stacks" line off the resume body.
        var split = FastFeed.SplitTrailing(req.ResumeText ?? "");
        var resumeBody = split.ResumePart;   // still carries any [FolderName]: line the macro reads
        var parsed = split.Parsed;
        var filename1 = parsed?.ResumeId ?? "";

        // 2) Run the active profile's Word macro on the resume body (background, no clipboard).
        var profile = _profileContext.Current;
        var docm = (profile?.WordDocPath ?? "").Trim();
        var macro = (profile?.MacroName ?? "").Trim();
        WordMacroService.Result macroResult;
        if (string.IsNullOrEmpty(docm) || string.IsNullOrEmpty(macro))
        {
            macroResult = new WordMacroService.Result(false,
                $"Profile '{profile?.Name}' needs a Word doc path + macro name (Profiles tab).");
            _activity.Warning("Resume", "Macro skipped", macroResult.Message);
        }
        else
        {
            macroResult = await _wordMacro.RunAsync(resumeBody, docm, macro, profile!.Name);
            if (macroResult.Success) _activity.Success("Resume", "Resume generated", $"{job.Url}");
            else _activity.Error("Resume", "Macro failed", macroResult.Message);
        }

        // 3) Auto-record the bid for this URL under the active profile.
        try
        {
            var existing = await _bids.FindLinkByNormalizedUrlAsync(job.Url);
            var link = existing ?? await _bids.AddLinkAsync(job.Url, req.JobDescription ?? "");
            var bid = await _bids.UpsertBidAsync(link.Id, b =>
            {
                if (!string.IsNullOrEmpty(req.JobDescription)) b.JobDescription = req.JobDescription;
                if (!string.IsNullOrEmpty(resumeBody)) b.GptResumeContent = resumeBody;
                b.Origin = "Resume Auto";
                if (parsed != null)
                {
                    b.ResumeId = parsed.ResumeId;
                    b.Company = parsed.Company;
                    b.Role = parsed.Role;
                    b.PrimaryStacks = parsed.PrimaryStacks.ToList();
                    b.Status = BidStatuses.Applied;
                }
            });
            var label = parsed != null ? $"{parsed.Company} · {parsed.Role}".Trim(' ', '·') : job.Url;
            _activity.Success("Resume", "Bid recorded", label);
            try { OnExtensionBidRecorded?.Invoke(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _activity.Error("Resume", "Bid record failed", ex.Message);
        }

        // 4) Finalize the job. Done even if the macro failed but the bid recorded — the
        //    resume file is the only casualty, and the Activity log captured why.
        if (macroResult.Success)
            await _resumeQueue.CompleteAsync(jobId, req.JobDescription ?? "", resumeBody, split.FastFeedLine, filename1, "");
        else
            await _resumeQueue.FailAsync(jobId, macroResult.Message);

        await WriteJsonAsync(ctx, 200, new { ok = true, macro = macroResult.Success, fastFeedApplied = parsed != null });
    }

    private async Task HandleResumeFailAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx);
        if (body == null) { await WriteBodyTooLargeAsync(ctx); return; }
        ResumeFailRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<ResumeFailRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = $"Invalid JSON: {ex.Message}" });
            return;
        }
        if (req == null || string.IsNullOrWhiteSpace(req.JobId) || !ObjectId.TryParse(req.JobId, out var jobId))
        {
            await WriteJsonAsync(ctx, 400, new { error = "jobId is required" });
            return;
        }
        await _resumeQueue.FailAsync(jobId, string.IsNullOrWhiteSpace(req.Error) ? "Extension reported failure." : req.Error!);
        _activity.Warning(ExtensionSource, "Resume job failed", req.Error ?? "", silent: true);
        await WriteJsonAsync(ctx, 200, new { ok = true });
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
    ///
    /// <para>
    /// The actual automation is serialized behind <see cref="_refreshWordLock"/> — see that
    /// field's doc comment. <c>chromeHwnd</c> is captured before the lock wait (not after) so
    /// a request queued behind another still restores focus to the window that actually
    /// clicked purple, rather than whatever happens to be focused once its turn comes up.
    /// </para>
    /// </summary>
    private async Task HandleRefreshWordAsync(HttpListenerContext ctx)
    {
        // Drain the request body even though we don't read it — leaving it unread can wedge
        // some HttpListener clients waiting for the response.
        _ = await ReadBodyAsync(ctx);

        var chromeHwnd = KeyboardHelper.GetForegroundWindow();

        var s = await _settingsService.GetAsync();
        var profile = _profileContext.Current;
        var wordPath = (profile?.WordDocPath ?? "").Trim();
        var wordHotkey = string.IsNullOrWhiteSpace(s.WordHotkey) ? "F9" : s.WordHotkey.Trim();

        if (string.IsNullOrWhiteSpace(wordPath))
        {
            var detail = profile == null
                ? "No active profile — create one in the Profiles tab first."
                : $"Set the Word document path for profile '{profile.Name}' in the Profiles tab first.";
            _activity.Warning(ExtensionSource, "Refresh Word failed", detail);
            await WriteJsonAsync(ctx, 400, new { success = false, error = detail });
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

        // Depth is claimed only after validation, so a misconfigured profile fails fast instead
        // of taking a queue slot from a caller that could actually have run.
        var depth = Interlocked.Increment(ref _refreshWordQueueDepth);
        try
        {
            if (depth > MaxRefreshWordQueueDepth)
            {
                var busy = $"{depth - 1} Word refreshes already queued — this one was dropped rather than left to time out. Try again in a moment.";
                _activity.Warning(ExtensionSource, "Word refresh rejected", busy);
                await WriteJsonAsync(ctx, 503, new { success = false, error = busy });
                return;
            }

            if (depth > 1)
            {
                _activity.Info(ExtensionSource, "Word refresh queued",
                    $"{depth - 1} refresh{(depth - 1 == 1 ? "" : "es")} ahead of this one.", silent: true);
            }

            // Bounded wait: a Word instance wedged on a modal dialog holds the lock for as long
            // as the user leaves it there, and an unbounded wait here would turn that into a
            // permanent outage for every other Chrome profile.
            if (!await _refreshWordLock.WaitAsync(RefreshWordQueueTimeout))
            {
                var stuck = $"Timed out after {RefreshWordQueueTimeout.TotalSeconds:0}s waiting for the previous Word refresh. Check whether Word is showing a dialog.";
                _activity.Error(ExtensionSource, "Word refresh timed out", stuck);
                await WriteJsonAsync(ctx, 503, new { success = false, error = stuck });
                return;
            }

            try
            {
                using var automationCts = new CancellationTokenSource(RefreshWordAutomationTimeout);
                var ct = automationCts.Token;
                var (mods, keyVk) = parsed.Value;

                var wordHwnd = await KeyboardHelper.OpenWordDocumentAsync(wordPath, ct);
                if (wordHwnd == IntPtr.Zero)
                {
                    _activity.Error(ExtensionSource, "Refresh Word failed", "Couldn't open the Word document. Check the path and that Word is installed.");
                    await WriteJsonAsync(ctx, 500, new { success = false, error = "Failed to open Word. Ensure Microsoft Word is installed and the path is correct." });
                    return;
                }
                await Task.Delay(KeyboardHelper.RefreshWordOpenDelayMs, ct);

                // Fast path: no modifiers → PostMessage straight to the window without focus juggling.
                if (mods.Count == 0 && KeyboardHelper.PostSingleKeyToWindow(wordHwnd, mods, keyVk))
                {
                    if (await KeyboardHelper.WaitForWordCloseAsync(8, ct))
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
                    await Task.Delay(KeyboardHelper.HOTKEY_DELAY_MS, ct);
                    KeyboardHelper.PressHotkey(mods, keyVk);
                    if (await KeyboardHelper.WaitForWordCloseAsync(KeyboardHelper.WORD_CLOSE_TIMEOUT_SECONDS, ct))
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
            catch (OperationCanceledException)
            {
                // Automation watchdog. Hand focus back so the user isn't left staring at Word.
                KeyboardHelper.ReturnToChrome(chromeHwnd);
                var timedOut = $"Word automation exceeded {RefreshWordAutomationTimeout.TotalSeconds:0}s and was abandoned. The bid itself is recorded separately and is unaffected.";
                _activity.Error(ExtensionSource, "Refresh Word timed out", timedOut);
                await WriteJsonAsync(ctx, 504, new { success = false, error = timedOut });
            }
            catch (Exception ex)
            {
                _activity.Error(ExtensionSource, "Refresh Word crashed", ex.Message);
                await WriteJsonAsync(ctx, 500, new { success = false, error = $"Word refresh failed: {ex.Message}" });
            }
            finally
            {
                _refreshWordLock.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _refreshWordQueueDepth);
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

    /// <summary>Body the extension POSTs to <c>/resume/result</c> after ChatGPT finishes a job.</summary>
    public class ResumeResultRequest
    {
        public string JobId { get; set; } = "";
        public string? JobDescription { get; set; }
        public string? ResumeText { get; set; }
    }

    /// <summary>Body the extension POSTs to <c>/resume/fail</c> when a job can't be processed.</summary>
    public class ResumeFailRequest
    {
        public string JobId { get; set; } = "";
        public string? Error { get; set; }
    }
}
