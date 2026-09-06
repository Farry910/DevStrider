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
/// can POST to.
///
/// <para>
/// It binds loopback only, and that is what stands in for authentication: nothing off this
/// machine can reach it. Requests arrive with no credential and are served as whoever is signed
/// in to the app, so the listener must not start before login — see <c>App.StartAfterLoginAsync</c>.
/// </para>
///
/// <para>
/// Lifecycle: created once by the DI container, <see cref="Start"/> called after a successful
/// sign-in on <see cref="Models.AppSettings.ListenerPort"/>, stopped via <see cref="StopAsync"/>
/// in <c>App.OnExit</c>. It uses <see cref="HttpListener"/> (built into .NET, zero extra
/// dependencies) and processes requests on the thread pool.
/// </para>
/// </summary>
public sealed partial class LocalApiServer : ObservableObject
{
    private const string ExtensionSource = "Extension";

    private readonly BidBoardService _bids;
    private readonly SettingsService _settingsService;
    private readonly ActivityLogService _activity;
    private readonly ProfileContext _profileContext;
    private readonly WordMacroService _wordMacro;

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
        WordMacroService wordMacro)
    {
        _bids = bids;
        _settingsService = settingsService;
        _activity = activity;
        _profileContext = profileContext;
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

            // Each request fans out to the thread pool so a slow database insert doesn't block the
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

            if (ctx.Request.HttpMethod == "POST" && path == "/generate-resume")
            {
                await HandleGenerateResumeAsync(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/prewarm")
            {
                await HandlePrewarmAsync(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && path == "/active-profile")
            {
                // The extension opens a *fresh* ChatGPT chat per bid, so it has to send the
                // profile's resume prompt ahead of the JD — a blank chat has no idea what output
                // shape we expect back.
                var p = _profileContext.Current;
                await WriteJsonAsync(ctx, 200, new
                {
                    name = p?.Name ?? "",
                    resumePrompt = (p?.ResumePrompt ?? "").Trim(),
                    hasMacro = !string.IsNullOrWhiteSpace(p?.WordDocPath)
                });
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

        // Capture-or-join by exact normalized URL (strict matching — different queries are
        // different postings, per the rule the user locked earlier). The posting and the bid are
        // one row, so this is a single call rather than a link lookup followed by a bid upsert.
        var (bid, joinedExisting) = await _bids.CaptureAsync(req.Url, req.JobDescription ?? "", b =>
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
            joinedExisting ? "Bid updated" : "Bid recorded",
            string.IsNullOrEmpty(label) ? req.Url : label);

        // Nudge the Bid Board to reload so the new/updated row appears without manual refresh.
        try { OnExtensionBidRecorded?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LocalApi] subscriber threw: " + ex.Message); }

        await WriteJsonAsync(ctx, joinedExisting ? 200 : 201, new
        {
            ok = true,
            bid = new { id = bid.Id.ToString(), bid.Url, bid.Status, bid.ResumeId, bid.Company, bid.Role },
            joinedExisting,
            fastFeedApplied = parsed != null
        });
    }


    /// <summary>
    /// Fired by the extension the instant the prompt is submitted to ChatGPT, so Word can be
    /// launched and the template opened during the thirty-odd seconds ChatGPT spends writing.
    /// That work used to happen after the reply landed, with the user watching.
    ///
    /// <para>
    /// Answers immediately and never fails the caller: a prewarm is an optimisation, and
    /// <see cref="HandleGenerateResumeAsync"/> opens whatever is still missing. Blocking here
    /// would stall the extension's own generation loop for the sake of work it doesn't need yet.
    /// </para>
    /// </summary>
    private async Task HandlePrewarmAsync(HttpListenerContext ctx)
    {
        _ = await ReadBodyAsync(ctx);

        var docm = (_profileContext.Current?.WordDocPath ?? "").Trim();
        if (!string.IsNullOrEmpty(docm))
        {
            // Deliberately not awaited — the response goes back now, Word warms up behind it.
            _ = Task.Run(() => _wordMacro.PrewarmAsync(docm));
        }

        await WriteJsonAsync(ctx, 200, new { ok = true, warming = !string.IsNullOrEmpty(docm) });
    }

    /// <summary>
    /// The one-button flow: the extension hands over a finished ChatGPT reply, we build the
    /// resume file and record the bid — all without touching the foreground.
    ///
    /// <para>
    /// <see cref="WordMacroService"/> invokes the macro over COM with <c>Visible = false</c>, so
    /// the user keeps typing into the job application while the resume is produced behind them.
    /// This replaced <c>/refresh-word</c>, which drove Word by synthesizing a configured hotkey
    /// into the foreground window — that needed Word in front and stole focus mid-application,
    /// and it is gone along with the hotkey setting it read.
    /// </para>
    ///
    /// <para>
    /// The bid is recorded even when the macro fails: the resume file is one artifact, the bid
    /// record is the thing that must not be lost. Failures land in the Activity log with the
    /// reason, and the response reports both outcomes separately.
    /// </para>
    /// </summary>
    private async Task HandleGenerateResumeAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx);
        if (body == null) { await WriteBodyTooLargeAsync(ctx); return; }

        GenerateResumeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<GenerateResumeRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = $"Invalid JSON: {ex.Message}" });
            return;
        }
        if (req == null || string.IsNullOrWhiteSpace(req.Url))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = "url is required" });
            return;
        }

        // Trailing "UID, Company, Role, Stack1, …" line is metadata for the bid, not resume body.
        var split = FastFeed.SplitTrailing(req.ResumeText ?? "");
        var resumeBody = split.ResumePart;
        var parsed = split.Parsed;

        var profile = _profileContext.Current;
        var docm = (profile?.WordDocPath ?? "").Trim();
        // Blank falls back to the standard entry point every template ships with.
        var macro = string.IsNullOrWhiteSpace(profile?.MacroName)
            ? WordMacroService.DefaultMacroName
            : profile!.MacroName.Trim();

        WordMacroService.Result macroResult;
        if (string.IsNullOrEmpty(docm))
        {
            macroResult = new WordMacroService.Result(false,
                $"Profile '{profile?.Name}' needs a Word template path (Profiles tab).");
            _activity.Warning(ExtensionSource, "Macro skipped", macroResult.Message);
        }
        else if (string.IsNullOrWhiteSpace(resumeBody))
        {
            macroResult = new WordMacroService.Result(false, "ChatGPT returned no resume text.");
            _activity.Warning(ExtensionSource, "Macro skipped", macroResult.Message);
        }
        else
        {
            macroResult = await _wordMacro.RunAsync(resumeBody, docm, macro, profile!.Name, req.JobDescription ?? "");
            if (macroResult.Success) _activity.Success(ExtensionSource, "Resume generated", req.Url);
            else _activity.Error(ExtensionSource, "Macro failed", macroResult.Message);
        }

        // Record regardless of the macro outcome.
        try
        {
            var (bid, joinedExisting) = await _bids.CaptureAsync(req.Url, req.JobDescription ?? "", b =>
            {
                if (!string.IsNullOrEmpty(req.JobDescription)) b.JobDescription = req.JobDescription;
                if (!string.IsNullOrEmpty(resumeBody)) b.GptResumeContent = resumeBody;
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

            var label = parsed != null ? $"{parsed.Company} · {parsed.Role}".Trim(' ', '·') : req.Url;
            _activity.Success(ExtensionSource, joinedExisting ? "Bid updated" : "Bid recorded", label);
            try { OnExtensionBidRecorded?.Invoke(); } catch { /* subscriber problem isn't ours */ }

            // Now that the bid is safely recorded, drop the job description into the folder the
            // macro wrote the resume into, so the two live together. Deliberately after the record
            // and outside its failure path: this is a convenience file, and nothing about it should
            // be able to cost a bid. Skipped when the macro didn't run — there is no folder then,
            // and inventing one to hold a lone job description would only litter the output root.
            var jdFile = macroResult.Success
                ? JobDescriptionFile.Save(docm, split.FastFeedLine, req.JobDescription)
                : new JobDescriptionFile.Result(false, "", "The macro didn't run, so there is no resume folder.");

            if (jdFile.Written)
                _activity.Info(ExtensionSource, "Job description saved", jdFile.Path, silent: true);
            else if (macroResult.Success)
                _activity.Warning(ExtensionSource, "Job description not saved", jdFile.Message, silent: true);

            await WriteJsonAsync(ctx, 200, new
            {
                ok = true,
                macro = macroResult.Success,
                macroError = macroResult.Success ? null : macroResult.Message,
                fastFeedApplied = parsed != null,
                jobDescriptionSaved = jdFile.Written,
                jobDescriptionPath = jdFile.Written ? jdFile.Path : null,
                company = bid.Company ?? "",
                role = bid.Role ?? "",
                resumeId = bid.ResumeId ?? ""
            });
        }
        catch (Exception ex)
        {
            _activity.Error(ExtensionSource, "Bid record failed", ex.Message);
            await WriteJsonAsync(ctx, 500, new
            {
                ok = false,
                macro = macroResult.Success,
                error = $"Bid record failed: {ex.Message}"
            });
        }
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

    /// <summary>Body the extension POSTs to <c>/generate-resume</c> once ChatGPT has finished.</summary>
    public class GenerateResumeRequest
    {
        public string Url { get; set; } = "";
        public string? JobDescription { get; set; }
        /// <summary>Full ChatGPT reply, trailing fast-feed line included — we split it here.</summary>
        public string? ResumeText { get; set; }
        public string? Origin { get; set; }
    }

    /// <summary>
    /// Wire shape the extension posts. Unknown fields deserialize away, so an older extension
    /// build sending something this no longer names still records its bid.
    /// </summary>
    public class RecordBidRequest
    {
        public string Url { get; set; } = "";
        public string? JobDescription { get; set; }
        public string? GptResumeContent { get; set; }
        public string? FastFeedInput { get; set; }
        public string? Comment { get; set; }
        public string? Origin { get; set; }
    }
}
