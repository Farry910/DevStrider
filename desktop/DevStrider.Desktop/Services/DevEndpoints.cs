using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The <c>/dev/*</c> half of the local listener: everything needed to diagnose a run from outside
/// the app, without a debugger and without a rebuild.
///
/// <para>
/// The endpoints exist because the interesting failures all happen somewhere unobservable. A run
/// stalls, and the trace says "composer not found" or "value did not persist" — true, and useless,
/// because the question is always what the page actually looked like. These serve the answer: the
/// live state, the whole activity log, the DOM, a script hook into either browser, and a picture.
/// </para>
///
/// <para>
/// View models are resolved lazily through the provider rather than injected. They are singletons
/// created for the window, and taking a constructor dependency on them from a service the container
/// builds first is how a dependency cycle gets introduced by accident.
/// </para>
/// </summary>
public sealed class DevEndpoints
{
    private readonly IServiceProvider _services;
    private readonly DevBridge _bridge;
    private readonly ActivityLogService _activity;
    private readonly SettingsService _settings;

    public DevEndpoints(IServiceProvider services, DevBridge bridge,
        ActivityLogService activity, SettingsService settings)
    {
        _services = services;
        _bridge = bridge;
        _activity = activity;
        _settings = settings;
    }

    private const long MaxScriptBytes = 2L * 1024 * 1024;

    /// <summary>Serves the route if it is one of ours. Returns false so the caller can 404 it.</summary>
    public async Task<bool> TryHandleAsync(HttpListenerContext ctx, string path)
    {
        if (!path.StartsWith("/dev", StringComparison.Ordinal)) return false;
        // Current is null only before the startup load, which happens before the listener binds.
        // Treat "not loaded yet" as off rather than dereferencing it: the safe reading of an
        // unknown setting is the one that refuses.
        if (_settings.Current is not { DeveloperTools: true })
        {
            await WriteJsonAsync(ctx, 403, new
            {
                error = "Developer tools are off. Settings > Local listener > Developer tools.",
            });
            return true;
        }

        var query = ctx.Request.QueryString;
        switch (path)
        {
            case "/dev":
                await WriteJsonAsync(ctx, 200, Index());
                return true;

            case "/dev/state":
                await WriteJsonAsync(ctx, 200, await StateAsync());
                return true;

            case "/dev/log":
                await WriteJsonAsync(ctx, 200, Log(
                    Int(query["n"], 120, 1, 4000), query["level"] ?? "", query["q"] ?? ""));
                return true;

            case "/dev/browsers":
                await WriteJsonAsync(ctx, 200, await _bridge.DescribeAsync());
                return true;

            case "/dev/composer":
                await WriteRawEvalAsync(ctx, query["target"] ?? "chatgpt", ChatGptComposer.DiagnoseScript);
                return true;

            case "/dev/dom":
                await WriteRawEvalAsync(ctx, query["target"] ?? "job",
                    DomScript(query["selector"] ?? "body", Int(query["max"], 200_000, 1_000, 2_000_000)));
                return true;

            case "/dev/text":
                await WriteRawEvalAsync(ctx, query["target"] ?? "job",
                    TextScript(query["selector"] ?? "body", Int(query["max"], 40_000, 500, 500_000)));
                return true;

            case "/dev/eval":
                await EvalAsync(ctx);
                return true;

            case "/dev/shot":
                await ScreenshotAsync(ctx, query["target"] ?? "chatgpt");
                return true;

            case "/dev/command":
                await CommandAsync(ctx);
                return true;
        }

        await WriteJsonAsync(ctx, 404, new { error = "Unknown /dev route.", routes = Index() });
        return true;
    }

    private static object Index() => new
    {
        note = "Loopback-only development endpoints. Settings > Local listener > Developer tools turns them off.",
        routes = new[]
        {
            "GET  /dev/state                        version, sign-in, queue, review tabs, automation flags",
            "GET  /dev/log?n=120&level=warning&q=   activity + trace entries, newest first",
            "GET  /dev/browsers                     registered browsers and what each has open",
            "GET  /dev/composer?target=chatgpt      every composer candidate, scored, with reasons",
            "GET  /dev/dom?target=job&selector=body&max=200000    outerHTML",
            "GET  /dev/text?target=job&selector=body&max=40000    innerText",
            "POST /dev/eval   {\"target\":\"chatgpt\",\"script\":\"...\"}   run script, return its JSON",
            "GET  /dev/shot?target=chatgpt          PNG of what that browser is showing",
            "POST /dev/command {\"name\":\"stop\"}       start|start-one|stop|skip|clear-queue|requeue-failed",
            "                                       fail-link|fail-machinery (drives a real failure on the first queued link)",
            "                                       add-links|select-all|select-none|select-site (links=site)",
        },
    };

    private async Task<object> StateAsync()
    {
        var jobs = _services.GetService<JobBrowserViewModel>();
        var profiles = _services.GetService<ProfileContext>();
        var session = _services.GetService<SessionContext>();
        // Never null in practice — the listener binds after the startup load — but a state dump is
        // the last thing that should throw, so the defaults stand in rather than a dereference.
        var settings = _settings.Current ?? new Models.AppSettings();

        return await OnUiAsync<object>(() => new
        {
            version = typeof(DevEndpoints).Assembly.GetName().Version?.ToString() ?? "",
            signedIn = session?.IsAuthenticated ?? false,
            signedInAs = session?.Email ?? "",
            profile = profiles?.Current?.Name ?? "",
            settings = new
            {
                settings.ListenerPort,
                settings.DeveloperTools,
                settings.AutomaticRunInBackground,
                proxy = new { settings.ProxyEnabled, settings.ProxyScope, settings.ProxyAddress },
            },
            automation = jobs == null ? null : new
            {
                running = jobs.IsAutomaticQueueRunning,
                step = jobs.CurrentStep,
                status = jobs.StatusMessage,
                adapter = jobs.AdapterName,
                manualJdPhase = jobs.IsManualJobDescriptionPhase,
                readyForReview = jobs.IsReadyForReview,
                parkedForReview = jobs.ParkedReviewCount,
                current = jobs.CurrentQueueItem == null ? null : new
                {
                    id = jobs.CurrentQueueItem.Id,
                    jobs.CurrentQueueItem.Url,
                    jobs.CurrentQueueItem.Status,
                    jobs.CurrentQueueItem.Error,
                    jobs.CurrentQueueItem.Attempts,
                },
            },
            tabs = jobs?.Tabs.Select(tab => new { tab.WorkItemId, tab.Title, tab.Url }).ToArray(),
            queue = jobs?.JobQueue.Select(item => new
            {
                id = item.Id, item.Url, item.Status, item.Error, item.Attempts,
            }).ToArray(),
        });
    }

    private object Log(int take, string level, string contains)
    {
        var entries = _activity.Entries.ToArray()
            .Where(entry => level.Length == 0 ||
                            entry.Level.ToString().Contains(level, StringComparison.OrdinalIgnoreCase))
            .Where(entry => contains.Length == 0 ||
                            (entry.Title + " " + entry.Detail + " " + entry.Source)
                            .Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .Select(entry => new
            {
                at = entry.At.ToString("HH:mm:ss.fff"),
                level = entry.Level.ToString(),
                entry.Source,
                entry.Title,
                entry.Detail,
            })
            .ToArray();
        return new { count = entries.Length, entries };
    }

    /// <summary>
    /// Writes a script's result through untouched. WebView2 returns JSON already, so re-wrapping it
    /// would mean parsing something whose shape is the unknown being investigated.
    /// </summary>
    private async Task WriteRawEvalAsync(HttpListenerContext ctx, string target, string script)
    {
        var (ok, result, error) = await _bridge.EvalAsync(target, script);
        if (!ok) { await WriteJsonAsync(ctx, 400, new { error, target }); return; }
        await WriteAsync(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(result));
    }

    private async Task EvalAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            await WriteJsonAsync(ctx, 405, new { error = "POST {\"target\":\"chatgpt\",\"script\":\"...\"}" });
            return;
        }
        var body = await ReadBodyAsync(ctx);
        string target, script;
        try
        {
            using var document = JsonDocument.Parse(body);
            target = document.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "chatgpt" : "chatgpt";
            script = document.RootElement.TryGetProperty("script", out var s) ? s.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = "body is not JSON: " + ex.Message });
            return;
        }
        if (script.Trim().Length == 0)
        {
            await WriteJsonAsync(ctx, 400, new { error = "script is empty" });
            return;
        }
        await WriteRawEvalAsync(ctx, target, script);
    }

    private async Task ScreenshotAsync(HttpListenerContext ctx, string target)
    {
        var (ok, png, error) = await _bridge.ScreenshotAsync(target);
        if (!ok) { await WriteJsonAsync(ctx, 400, new { error, target }); return; }
        await WriteAsync(ctx, 200, "image/png", png);
    }

    private async Task CommandAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            await WriteJsonAsync(ctx, 405, new { error = "POST {\"name\":\"stop\"}" });
            return;
        }
        var body = await ReadBodyAsync(ctx);
        string name, argument;
        try
        {
            using var document = JsonDocument.Parse(body.Trim().Length == 0 ? "{}" : body);
            name = (document.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Trim();
            argument = document.RootElement.TryGetProperty("links", out var l) ? l.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = "body is not JSON: " + ex.Message });
            return;
        }

        var jobs = _services.GetService<JobBrowserViewModel>();
        if (jobs == null) { await WriteJsonAsync(ctx, 503, new { error = "the job browser is not up yet" }); return; }

        var ran = await OnUiAsync(() =>
        {
            switch (name.ToLowerInvariant())
            {
                case "start": jobs.StartAutomaticQueueCommand.Execute(null); return "start";
                case "stop": jobs.StopAutomaticQueueCommand.Execute(null); return "stop";
                case "skip": jobs.SkipCurrentQueuedLinkCommand.Execute(null); return "skip";
                case "clear-queue": jobs.ClearQueuedLinksCommand.Execute(null); return "clear-queue";
                case "requeue-failed": jobs.RequeueFailedLinksCommand.Execute(null); return "requeue-failed";
                case "add-links":
                    jobs.QueueLinksInput = argument;
                    jobs.AddLinksToQueueCommand.Execute(null);
                    return "add-links";
                case "start-one": jobs.StartSingleLinkCommand.Execute(null); return "start-one";
                case "paste-jd":
                    jobs.PastedJobDescription = argument;
                    jobs.ApplyPastedJobDescriptionCommand.Execute(null);
                    return "paste-jd";
                case "select-all": jobs.SelectAllLinksCommand.Execute(null); return "select-all";
                case "select-none": jobs.ClearLinkSelectionCommand.Execute(null); return "select-none";
                // Drives the real failure path against the first queued link, so the handoff from
                // an automatic run to the manual board can be exercised without applying to
                // anybody's posting. "link" is the posting-level failure that moves a link to the
                // manual board; "machinery" is the kind that stays Failed and retryable. Both call
                // the same MarkAutomationFailureAsync the run itself calls — a simulated *cause*,
                // not a simulated effect, which is the only version of this worth having.
                case "fail-link":
                case "fail-machinery":
                {
                    var target = jobs.JobQueue.FirstOrDefault(item =>
                        item.Status == Models.JobLinkQueueStatuses.Queued);
                    if (target == null) return "fail: nothing is queued";
                    var scope = name.Equals("fail-link", StringComparison.OrdinalIgnoreCase)
                        ? JobBrowserViewModel.FailureScope.Link
                        : JobBrowserViewModel.FailureScope.Machinery;
                    _ = jobs.MarkAutomationFailureAsync(target.Id,
                        $"simulated {scope} failure from /dev/command", scope);
                    return $"{name} on {target.Url}";
                }
                default: return "";
            }
        });

        if (ran.Length == 0)
        {
            await WriteJsonAsync(ctx, 400, new
            {
                error = $"unknown command \"{name}\"",
                known = new[] { "start", "start-one", "stop", "skip", "clear-queue", "requeue-failed",
                    "add-links", "select-all", "select-none", "select-site",
                    "fail-link", "fail-machinery" },
            });
            return;
        }
        _activity.Info("Dev", "Command from /dev/command", ran, silent: true);
        // Commands are asynchronous inside the app, so the state that comes back is a snapshot
        // taken right after dispatch, not the settled result. Poll /dev/state for that.
        await WriteJsonAsync(ctx, 200, new { ran, state = await StateAsync() });
    }

    private static string DomScript(string selector, int max)
    {
        var payload = JsonSerializer.Serialize(new { selector, max });
        return """
(() => {
 const request = __PAYLOAD__;
 const nodes = Array.from(document.querySelectorAll(request.selector));
 if (!nodes.length) return { ok:false, error:'no element matched ' + request.selector, url:location.href };
 const html = nodes.map(n => n.outerHTML).join('\n<!-- next match -->\n');
 return { ok:true, url:location.href, title:document.title, matched:nodes.length,
   truncated: html.length > request.max, length:html.length, html:html.slice(0, request.max) };
})()
""".Replace("__PAYLOAD__", payload);
    }

    private static string TextScript(string selector, int max)
    {
        var payload = JsonSerializer.Serialize(new { selector, max });
        return """
(() => {
 const request = __PAYLOAD__;
 const nodes = Array.from(document.querySelectorAll(request.selector));
 if (!nodes.length) return { ok:false, error:'no element matched ' + request.selector, url:location.href };
 const text = nodes.map(n => n.innerText || n.textContent || '').join('\n---\n');
 return { ok:true, url:location.href, title:document.title, matched:nodes.length,
   truncated: text.length > request.max, length:text.length, text:text.slice(0, request.max) };
})()
""".Replace("__PAYLOAD__", payload);
    }

    private static async Task<T> OnUiAsync<T>(Func<T> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return work();
        return await dispatcher.InvokeAsync(work);
    }

    private static int Int(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>
    /// Read a body with the cap enforced <em>while</em> reading. The declared Content-Length is a
    /// hint only — a chunked request reports -1, which sailed past the up-front check and then
    /// buffered as much as the caller cared to send. Same loop the main listener uses.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxScriptBytes) return "";

        var buffer = new byte[81920];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await ctx.Request.InputStream.ReadAsync(buffer)) > 0)
        {
            if (accumulated.Length + read > MaxScriptBytes) return "";
            accumulated.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(accumulated.ToArray());
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        // Relaxed escaping because this is read by a person in a terminal. The default encoder turns
        // every & and > in a job URL into & and >, which is safe for a browser and awful
        // for the one audience these endpoints have.
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        await WriteAsync(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.LongLength;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }
}
