using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The seam that makes this app diagnosable from outside itself.
///
/// <para>
/// Almost everything DevStrider does happens inside two embedded browsers on a signed-in session
/// that cannot be reproduced anywhere else: ChatGPT's live DOM, and a job board's live application
/// form. When one of them changes shape, the only evidence that ever reached a bug report was a
/// trace line saying something was not found — which never says what <em>was</em> there. Diagnosis
/// then means guessing at a page nobody can see, shipping a build, and waiting for the next run.
/// </para>
///
/// <para>
/// This registry lets a caller on loopback run a script inside either browser, read its DOM and
/// take its picture, on the real session, without touching the app. It is the difference between
/// reasoning about a page and looking at one.
/// </para>
///
/// <para>
/// Views register themselves as they initialise and hand over an accessor rather than the browser,
/// because the job-site browser is whichever tab automation currently holds and that changes under
/// the caller. Everything here marshals to the UI thread: WebView2 is single-threaded and the HTTP
/// listener answers on the thread pool.
/// </para>
/// </summary>
public sealed class DevBridge
{
    private readonly Dictionary<string, Func<CoreWebView2?>> _browsers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Registers, or replaces, the accessor for a named browser.</summary>
    public void Register(string name, Func<CoreWebView2?> resolve)
    {
        lock (_gate) _browsers[name] = resolve;
    }

    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _browsers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(); }
    }

    private Func<CoreWebView2?>? Resolver(string name)
    {
        lock (_gate) return _browsers.TryGetValue(name, out var resolve) ? resolve : null;
    }

    /// <summary>
    /// Runs work on the UI thread. A WebView2 touched from the listener's thread throws, and the
    /// throw surfaces as a plain 500 that says nothing about why.
    /// </summary>
    private static async Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return await work();
        if (dispatcher.CheckAccess()) return await work();
        return await await dispatcher.InvokeAsync(work);
    }

    /// <summary>What each registered browser currently has open.</summary>
    public async Task<object> DescribeAsync() =>
        await OnUiAsync<object>(async () =>
        {
            var described = new List<object>();
            foreach (var name in Names)
            {
                var core = Resolver(name)?.Invoke();
                described.Add(new
                {
                    name,
                    ready = core != null,
                    url = core?.Source ?? "",
                    title = core?.DocumentTitle ?? "",
                });
            }
            return await Task.FromResult(new { browsers = described });
        });

    /// <summary>
    /// Evaluates a script in a named browser and returns its JSON result verbatim. The result is
    /// whatever WebView2 gives back, including its <c>null</c> for a script that threw — the raw
    /// form is deliberate, because a wrapper that tidied it would hide the failure being chased.
    /// </summary>
    public async Task<(bool Ok, string Result, string Error)> EvalAsync(string name, string script)
    {
        var resolve = Resolver(name);
        if (resolve == null)
            return (false, "", $"no browser named \"{name}\". Known: {string.Join(", ", Names)}");

        return await OnUiAsync(async () =>
        {
            var core = resolve();
            if (core == null) return (false, "", $"the \"{name}\" browser is not initialised yet");
            try { return (true, await core.ExecuteScriptAsync(script), ""); }
            catch (Exception ex) { return (false, "", ex.Message); }
        });
    }

    /// <summary>
    /// A PNG of what a browser is showing. A screenshot answers "is there a dialog over it?" in one
    /// look, and that question has cost more runs than any other.
    /// </summary>
    public async Task<(bool Ok, byte[] Png, string Error)> ScreenshotAsync(string name)
    {
        var resolve = Resolver(name);
        if (resolve == null)
            return (false, [], $"no browser named \"{name}\". Known: {string.Join(", ", Names)}");

        return await OnUiAsync(async () =>
        {
            var core = resolve();
            if (core == null) return (false, Array.Empty<byte>(), $"the \"{name}\" browser is not initialised yet");
            try
            {
                var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                    JsonSerializer.Serialize(new { format = "png" }));
                using var document = JsonDocument.Parse(json);
                var data = document.RootElement.TryGetProperty("data", out var value) ? value.GetString() : null;
                if (string.IsNullOrEmpty(data)) return (false, Array.Empty<byte>(), "the browser returned no image data");
                return (true, Convert.FromBase64String(data), "");
            }
            catch (Exception ex) { return (false, Array.Empty<byte>(), ex.Message); }
        });
    }
}
