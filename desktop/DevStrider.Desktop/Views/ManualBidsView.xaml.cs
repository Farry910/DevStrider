using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DevStrider.Desktop.Views;

/// <summary>
/// The Manual Bids tab's browser and its one automated action: attaching a finished resume.
///
/// <para>
/// This tab owns its browsers outright — its own environment, its own user-data folder, its own
/// host panel. Nothing here is shared with the Job Browser, which is what lets an automatic run
/// and a manual bid proceed at the same time without either waiting on the other for a tab.
/// </para>
///
/// <para>
/// Nothing drives these pages. There is no adapter, no form hunt, no fill and no submit — the one
/// thing the app does to a page in this tab is put a file into an upload field, and only when
/// asked. A person is doing the rest.
/// </para>
/// </summary>
public partial class ManualBidsView : UserControl
{
    private readonly Dictionary<Guid, WebView2> _browsers = new();
    private CoreWebView2Environment? _environment;
    private ManualBidsViewModel? _attached;

    private BidTraceService? Trace => (DataContext as ManualBidsViewModel)?.Trace;

    public ManualBidsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (ReferenceEquals(_attached, DataContext)) return;
        Detach();
        if (DataContext is not ManualBidsViewModel vm) return;
        _attached = vm;
        vm.OpenRequested += OpenAsync;
        vm.AttachRequested += AttachResumeAsync;
        vm.QuestionsRequested += ReadQuestionsAsync;
        vm.FillRequested += FillAsync;
    }

    private void Detach()
    {
        if (_attached == null) return;
        _attached.OpenRequested -= OpenAsync;
        _attached.AttachRequested -= AttachResumeAsync;
        _attached.QuestionsRequested -= ReadQuestionsAsync;
        _attached.FillRequested -= FillAsync;
        _attached = null;
    }

    /// <summary>
    /// A separate user-data folder from the job-site browser the automatic run uses.
    ///
    /// <para>
    /// Two environments cannot share one folder, and these are two environments because they are
    /// two tabs. It also means a site you are signed into by hand here does not disturb whatever
    /// the run has, and the other way round.
    /// </para>
    /// </summary>
    private async Task<WebView2> CreateBrowserAsync()
    {
        var proxy = new ProxyConfiguration((DataContext as ManualBidsViewModel)?.ProxySettings);
        if (_environment == null)
        {
            var arguments = BrowserLaunch.Arguments(proxy, forChatGpt: false);
            _environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DevStrider", "webview2", "manual-bids"),
                options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments });
        }

        var browser = new WebView2 { Visibility = Visibility.Hidden };
        BrowserHost.Children.Add(browser);
        await browser.EnsureCoreWebView2Async(_environment);
        if (proxy.AppliesToJobSites)
            ProxyConfiguration.AttachCredentials(browser.CoreWebView2, proxy,
                (message, detail) => Trace?.Step("Manual", message, detail));
        return browser;
    }

    /// <summary>Opens a posting in its own browser and stops there.</summary>
    private async Task OpenAsync(Guid workItemId, string url)
    {
        if (DataContext is not ManualBidsViewModel vm) return;
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            vm.StatusMessage = $"That link is not a web address: {url}";
            return;
        }
        try
        {
            if (!_browsers.TryGetValue(workItemId, out var browser))
            {
                browser = await CreateBrowserAsync();
                _browsers[workItemId] = browser;
            }
            ShowOnly(browser);
            if (browser.CoreWebView2 != null &&
                !string.Equals(browser.CoreWebView2.Source, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            Trace?.Step("Manual", "posting opened, hands off", uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Couldn't open the posting: {ex.Message}";
            Trace?.Warn("Manual", "could not open the posting", ex.Message);
        }
    }

    /// <summary>
    /// Where this bid browser is pointed. Empty when it was never opened, which is the case the
    /// caller has to tell apart: there is no form on screen to hand over.
    /// </summary>
    private string CurrentUrlFor(Guid workItemId) =>
        _browsers.TryGetValue(workItemId, out var browser) ? browser.CoreWebView2?.Source ?? "" : "";


    /// <summary>
    /// Reads the form on screen with the same script the automatic run uses.
    ///
    /// <para>
    /// The scripts in <see cref="JobSiteFormAdapters"/> are static builders that take a URL and
    /// return JavaScript, so they run against any browser. That is what lets this tab do the same
    /// reading and filling without a second copy of the engine or a share of the other tab.
    /// </para>
    /// </summary>
    private async Task<string> ReadQuestionsAsync(Guid workItemId)
    {
        if (!_browsers.TryGetValue(workItemId, out var browser) || browser.CoreWebView2 == null) return "[]";
        var json = await browser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
        Trace?.Step("Manual", "questions read", (json ?? "").Length + " chars");
        return string.IsNullOrWhiteSpace(json) ? "[]" : json;
    }

    /// <summary>
    /// Types the answers into the form on screen and reports what landed.
    ///
    /// <para>
    /// Values go in through the same fill script the run uses, which reaches text boxes, radios and
    /// checkboxes. Comboboxes and custom dropdowns need the click choreography the automatic path
    /// has, and are left for the person - which is the honest split, since they are also the ones
    /// most likely to be got wrong silently.
    /// </para>
    /// </summary>
    private async Task<string> FillAsync(Guid workItemId, IReadOnlyDictionary<string, string> answers)
    {
        if (!_browsers.TryGetValue(workItemId, out var browser) || browser.CoreWebView2 == null)
            return "That bid has no browser open.";
        if (!Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out var uri))
            return "That tab is not on a page.";
        ShowOnly(browser);
        var core = browser.CoreWebView2;

        // Pass one: everything the script can set directly - radios, checkboxes, native selects.
        // It also *plans* the text fields rather than typing them, which is why pass two exists.
        var raw = await core.ExecuteScriptAsync(JobSiteFormAdapters.BuildFillScript(uri, answers));
        Trace?.Payload("Manual", "fill result", raw ?? "");
        int direct = 0, skipped = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw ?? "{}");
            // Numbers, not arrays. Reading them as arrays is why the first cut of this reported
            // "0 filled" on a form it had actually planned a dozen fields for.
            if (doc.RootElement.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.Number)
                direct = f.GetInt32();
            if (doc.RootElement.TryGetProperty("skipped", out var k) && k.ValueKind == JsonValueKind.Number)
                skipped = k.GetInt32();
        }
        catch (JsonException) { return "The form did not answer in a shape this could read."; }

        // Pass two: type the planned text fields the way a person would - click the field, send
        // real key events, then read it back. Controlled React inputs discard a value simply
        // assigned to them, which is what the plan-then-type split is for.
        var typed = 0; var planned = 0;
        var planJson = await core.ExecuteScriptAsync(JobSiteFormAdapters.TextFieldPlanScript);
        try
        {
            using var plan = JsonDocument.Parse(planJson ?? "[]");
            if (plan.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var entry in plan.RootElement.EnumerateArray())
                {
                    planned++;
                    var index = entry.GetProperty("index").GetInt32();
                    var label = entry.GetProperty("label").GetString() ?? "";
                    var value = entry.GetProperty("value").GetString() ?? "";

                    var targetJson = await core.ExecuteScriptAsync(
                        JobSiteFormAdapters.BuildTextFieldTargetScript(index));
                    using var target = JsonDocument.Parse(targetJson ?? "{}");
                    if (!target.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) continue;

                    await JobBrowserView.DispatchMouseClickAsync(core,
                        target.RootElement.GetProperty("x").GetDouble(),
                        target.RootElement.GetProperty("y").GetDouble());
                    await JobBrowserView.DispatchTextEntryAsync(core, value);
                    await Task.Delay(600);

                    var verifiedJson = await core.ExecuteScriptAsync(
                        JobSiteFormAdapters.BuildTextFieldVerifyScript(index));
                    using var verified = JsonDocument.Parse(verifiedJson ?? "{}");
                    if (verified.RootElement.TryGetProperty("ok", out var vok) && vok.GetBoolean()) typed++;
                    else Trace?.Warn("Manual", $"value did not persist for \"{label}\"", "left for you");
                }
        }
        catch (JsonException) { /* the plan is optional; pass one still counts */ }

        var note = $"Filled {direct + typed} field(s)";
        if (planned > typed) note += $", {planned - typed} text field(s) would not take a value";
        if (skipped > 0) note += $", {skipped} skipped";
        // Comboboxes and custom dropdowns need the click choreography the automatic path carries.
        // They are left alone here rather than half-set, which is the failure that is hard to spot.
        return note + ". Dropdowns are left for you.";
    }

    private void ShowOnly(WebView2 browser)
    {
        foreach (var child in BrowserHost.Children.OfType<WebView2>())
            child.Visibility = ReferenceEquals(child, browser) ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>
    /// Puts the finished resume into the upload field on that bid's own page.
    ///
    /// <para>
    /// Scoped to the bid's browser rather than whichever is in front, because several can be open
    /// and putting a resume into the wrong application's form is the one mistake here that is not
    /// obvious afterwards.
    /// </para>
    /// </summary>
    private async Task AttachResumeAsync(Guid workItemId, string resumePath)
    {
        if (DataContext is not ManualBidsViewModel vm) return;
        if (!_browsers.TryGetValue(workItemId, out var browser) || browser.CoreWebView2 == null)
        {
            vm.StatusMessage = "Open that bid's posting first, then attach.";
            return;
        }
        if (!File.Exists(resumePath))
        {
            vm.StatusMessage = "That resume file is no longer on disk.";
            return;
        }
        ShowOnly(browser);

        try
        {
            var input = await FindResumeFileInputAsync(browser.CoreWebView2);
            if (input == null)
            {
                vm.StatusMessage = "No upload field was found on this page. Use \"Show file\" and attach it by hand.";
                return;
            }
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",
                    JsonSerializer.Serialize(new { files = new[] { resumePath }, backendNodeId = input.Value }))
                .WaitAsync(TimeSpan.FromSeconds(15));
            // The site is told the field changed, or a controlled form ignores what was set.
            await NotifyAsync(browser.CoreWebView2, input.Value).WaitAsync(TimeSpan.FromSeconds(10));
            vm.StatusMessage = "Resume attached. Check the form shows it, then submit when you are ready.";
            Trace?.Ok("Manual", "resume attached", resumePath);
        }
        catch (TimeoutException)
        {
            vm.StatusMessage = "The browser did not answer. Use \"Show file\" and attach it by hand.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Couldn't attach the resume: {ex.Message}";
            Trace?.Warn("Manual", "attach failed", ex.Message);
        }
    }

    /// <summary>The most likely resume upload field, scored the way the automatic path scores it.</summary>
    private static async Task<int?> FindResumeFileInputAsync(CoreWebView2 core)
    {
        await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
        var json = await core.CallDevToolsProtocolMethodAsync(
            "DOM.getFlattenedDocument", "{\"depth\":-1,\"pierce\":true}");
        using var document = JsonDocument.Parse(json);

        int? best = null;
        var bestScore = int.MinValue;
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("nodeName", out var nodeName)
                || !string.Equals(nodeName.GetString(), "INPUT", StringComparison.OrdinalIgnoreCase)
                || !node.TryGetProperty("attributes", out var attributes)) continue;

            var flat = string.Join(" ", attributes.EnumerateArray().Select(a => a.GetString() ?? "")).ToLowerInvariant();
            if (!flat.Contains("file")) continue;

            var score = 0;
            if (flat.Contains("resume")) score += 10;
            if (flat.Contains("cv")) score += 6;
            if (flat.Contains("cover")) score -= 8;   // the other upload on the same form
            if (flat.Contains("photo") || flat.Contains("avatar")) score -= 10;
            if (score <= bestScore) continue;
            bestScore = score;
            best = node.GetProperty("backendNodeId").GetInt32();
        }
        return best;
    }

    private static async Task NotifyAsync(CoreWebView2 core, int backendNodeId)
    {
        var resolved = await core.CallDevToolsProtocolMethodAsync("DOM.resolveNode",
            JsonSerializer.Serialize(new { backendNodeId }));
        using var document = JsonDocument.Parse(resolved);
        if (!document.RootElement.GetProperty("object").TryGetProperty("objectId", out var objectId)) return;
        await core.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", JsonSerializer.Serialize(new
        {
            objectId = objectId.GetString(),
            functionDeclaration =
                "function(){this.dispatchEvent(new Event('input',{bubbles:true}));"
                + "this.dispatchEvent(new Event('change',{bubbles:true}));}",
            returnByValue = true,
        }));
    }
}
