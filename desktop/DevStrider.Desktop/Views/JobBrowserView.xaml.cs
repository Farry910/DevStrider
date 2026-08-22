using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DevStrider.Desktop.Views;

public partial class JobBrowserView : UserControl
{
    private bool _initialized;

    public JobBrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevStrider", "webview2", "job-sites");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: path);
            await JobSiteBrowser.EnsureCoreWebView2Async(environment);
            JobSiteBrowser.CoreWebView2.SourceChanged += OnBrowserSourceChanged;
            if (DataContext is JobBrowserViewModel vm)
            {
                vm.QueueNavigationRequested += NavigateToQueuedLink;
                vm.ApplicationFillRequested += FillApplicationAutomatically;
            }
        }
        catch (Exception ex) when (DataContext is JobBrowserViewModel vm)
        {
            vm.StatusMessage = "Job browser couldn't start: " + ex.Message;
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || !TryGetHttpUri(vm.Address, out var uri))
        {
            if (DataContext is JobBrowserViewModel invalid) invalid.StatusMessage = "Enter a valid HTTP(S) address.";
            return;
        }
        JobSiteBrowser.CoreWebView2?.Navigate(uri.AbsoluteUri);
        vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
    }

    private async void NavigateToQueuedLink()
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null ||
            !TryGetHttpUri(vm.Address, out var uri)) return;
        try
        {
            vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
            await NavigateAsync(uri);
            if (!vm.IsAutomaticQueueRunning) return;
            await Task.Delay(900);
            vm.BeginPageExtraction();
            var (jobDescription, questions, gate) = await ExtractPageAsync(openApplication: true);
            if (!string.IsNullOrWhiteSpace(gate))
            {
                if (vm.CurrentQueueItem != null)
                    await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, gate);
                return;
            }
            await vm.AcceptExtractedPageAsync(jobDescription, questions);
        }
        catch (Exception ex)
        {
            if (vm.CurrentQueueItem != null)
                await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, "Could not read the job page: " + ex.Message);
        }
    }

    private async Task NavigateAsync(Uri uri)
    {
        var core = JobSiteBrowser.CoreWebView2 ?? throw new InvalidOperationException("The job browser is unavailable.");
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current) &&
            Uri.Compare(current, uri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0) return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= OnCompleted;
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException($"Navigation failed: {args.WebErrorStatus}"));
        }
        core.NavigationCompleted += OnCompleted;
        core.Navigate(uri.AbsoluteUri);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private void OnBrowserSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm ||
            !Uri.TryCreate(JobSiteBrowser.CoreWebView2?.Source, UriKind.Absolute, out var uri)) return;
        vm.Address = uri.AbsoluteUri;
        vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
    }

    private async Task<(string JobDescription, string QuestionsJson, string Gate)> ExtractPageAsync(bool openApplication)
    {
        var (gate, _) = await DetectHumanGateAsync();
        if (!string.IsNullOrWhiteSpace(gate)) return ("", "[]", gate);
        var rawText = await JobSiteBrowser.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        var jobDescription = JsonSerializer.Deserialize<string>(rawText) ?? "";
        if (openApplication)
        {
            var openResult = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.OpenApplicationScript);
            using var opened = JsonDocument.Parse(openResult);
            if (opened.RootElement.TryGetProperty("clicked", out var clicked) && clicked.GetBoolean())
            {
                await Task.Delay(1500);
                (gate, _) = await DetectHumanGateAsync();
                if (!string.IsNullOrWhiteSpace(gate)) return (jobDescription, "[]", gate);
            }
        }
        var questions = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
        return (LooksLikeJobDescription(jobDescription) ? jobDescription : "", questions, "");
    }

    /// <summary>
    /// Splits human work into a blocker (the page cannot be read or filled at all) and an advisory
    /// (something a human must finish before submitting, which never stops the run). Greenhouse and
    /// Ashby load score-based reCAPTCHA on every application: it renders a "grecaptcha-badge" element
    /// and an invisible anchor frame that no human ever touches, so their mere presence is not a gate.
    /// </summary>
    private async Task<(string Blocker, string Advisory)> DetectHumanGateAsync()
    {
        const string script = """
(() => {
 const shown = e => {
   if (!e) return false;
   const rect = e.getBoundingClientRect();
   if (rect.width < 8 || rect.height < 8) return false;
   for (let node = e; node && node !== document.documentElement; node = node.parentElement) {
     const style = getComputedStyle(node);
     if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
   }
   return true;
 };
 const body = String(document.body?.innerText || '').toLowerCase();
 const frames = Array.from(document.querySelectorAll('iframe'));
 const src = f => String(f.getAttribute('src') || '').toLowerCase();

 // Only a rendered challenge is human work. The v3/Enterprise probe is excluded twice over: it is
 // marked size=invisible and it lives inside the badge that Ashby explicitly hides.
 const recaptcha = frames.some(f => {
   const url = src(f);
   if (!url.includes('recaptcha') || url.includes('size=invisible')) return false;
   if (f.closest('.grecaptcha-badge')) return false;
   return (url.includes('/bframe') || url.includes('/anchor')) && shown(f);
 });
 const hcaptcha = frames.some(f => src(f).includes('hcaptcha.com') && shown(f));
 const turnstile = frames.some(f => src(f).includes('challenges.cloudflare.com') && shown(f));

 const fillable = Array.from(document.querySelectorAll('input:not([type="hidden"]):not([type="submit"]):not([type="button"]),textarea,select')).filter(shown);
 const password = Array.from(document.querySelectorAll('input[type="password"]')).filter(shown);
 const otp = Array.from(document.querySelectorAll('input')).filter(e => shown(e) &&
   /one-time-code|\botp\b|\bmfa\b|\b2fa\b|two.?factor|verification.?code|security.?code/i
     .test([e.autocomplete, e.name, e.id, e.getAttribute('aria-label'), e.placeholder].filter(Boolean).join(' ')));

 // A bot interstitial replaces the page outright, so no application field is reachable behind it.
 const interstitial = !fillable.length &&
   /just a moment|verify you are human|checking your browser|press and hold|enable javascript and cookies/.test(body);

 let blocker = '';
 if (interstitial) blocker = 'The site is showing a bot check instead of the page. Clear it in the job browser, then retry the current item.';
 else if (otp.length) blocker = 'Account verification or MFA requires human attention. Complete it, then retry the current item.';
 else if (password.length && fillable.length - password.length < 3) blocker = 'Job-site sign-in requires human attention. Sign in, then retry the current item.';

 const advisory = recaptcha || hcaptcha || turnstile
   ? 'A CAPTCHA is showing on this page; complete it yourself before submitting.'
   : '';
 return { blocker, advisory };
})()
""";
        var json = await JobSiteBrowser.ExecuteScriptAsync(script);
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.GetProperty("blocker").GetString() ?? "",
            document.RootElement.GetProperty("advisory").GetString() ?? "");
    }

    private static bool LooksLikeJobDescription(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 180;

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            var raw = await JobSiteBrowser.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            vm.JobDescription = JsonSerializer.Deserialize<string>(raw) ?? "";
            vm.StatusMessage = string.IsNullOrWhiteSpace(vm.JobDescription) ? "No visible page text was found." : "Visible page text extracted.";
        }
        catch (Exception ex) { vm.StatusMessage = "Could not extract page text: " + ex.Message; }
    }

    private async void OnStartBid(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null ||
            !TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out var uri))
        {
            if (DataContext is JobBrowserViewModel invalid) invalid.StatusMessage = "Open a job page first.";
            return;
        }
        try
        {
            var (jobDescription, questions, gate) = await ExtractPageAsync(openApplication: true);
            if (!string.IsNullOrWhiteSpace(gate))
            {
                vm.StatusMessage = gate;
                return;
            }
            if (string.IsNullOrWhiteSpace(jobDescription)) jobDescription = vm.JobDescription;
            if (string.IsNullOrWhiteSpace(jobDescription))
            {
                vm.StatusMessage = "No usable JD was found. Add the page to the queue and use the JD fallback.";
                return;
            }
            await vm.StartManualBidFromCurrentPageAsync(uri.AbsoluteUri, jobDescription, questions);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not start the application flow: " + ex.Message;
            vm.RecordFailure("Application start failed", ex.Message);
        }
    }

    private async void OnExtractQuestions(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            vm.FormQuestionsJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
            vm.StatusMessage = "Safe, unanswered application questions extracted.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not extract form questions: " + ex.Message;
            vm.RecordFailure("Question extraction failed", ex.Message);
        }
    }

    private async void FillApplicationAutomatically(ResumeAutomationResult result)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out _) && TryGetHttpUri(result.JobUrl, out var target))
                await NavigateAsync(target);

            // The shell has just brought this workspace forward. Let the layout pass finish so the
            // WebView is at its final size before any script measures the page.
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            var fill = await FillFieldsAsync(vm);
            var uploaded = false;
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.ResumeFilePath) && File.Exists(result.ResumeFilePath))
            {
                vm.SelectedResumePath = result.ResumeFilePath;
                uploaded = await UploadResumeAsync(vm, reportStatus: false);
            }
            else notes.Add("No generated resume file was found; choose it manually.");

            // A challenge that appears only once the form is filled is still the human's to solve at
            // submit time, so it belongs in the review note rather than failing a filled application.
            var (_, advisory) = await DetectHumanGateAsync();
            if (!string.IsNullOrWhiteSpace(advisory)) notes.Add(advisory);
            var outstanding = DescribeUnfilled(fill.Unfilled);
            if (!string.IsNullOrWhiteSpace(outstanding)) notes.Add(outstanding);

            await vm.MarkReadyForReviewAsync(fill.Adapter, fill.Filled, fill.Skipped, fill.Touched, uploaded,
                string.Join(" ", notes), fill.Unfilled);
        }
        catch (Exception ex)
        {
            await vm.MarkAutomationFailureAsync(result.WorkItemId, "Could not fill the application: " + ex.Message);
        }
    }

    private async Task<FillOutcome> FillFieldsAsync(JobBrowserViewModel vm)
    {
        if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var uri))
            throw new InvalidOperationException("No application page is open.");
        var values = vm.BuildFillValues();
        var json = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.BuildFillScript(uri, values));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Custom dropdowns are driven after the plain fields, because typing into one opens an
        // overlay that would sit on top of anything still to be filled.
        var (comboFilled, comboTouched) = await FillCustomDropdownsAsync(values);

        var outcome = new FillOutcome(
            root.GetProperty("adapter").GetString() ?? "Default (generic)",
            root.GetProperty("filled").GetInt32() + comboFilled,
            root.GetProperty("skipped").GetInt32(),
            StringList(root, "touched").Concat(comboTouched).ToArray(),
            StringList(root, "unfilled"));

        // Re-ask what is outstanding: the first answer was taken before the dropdowns were driven.
        if (comboFilled > 0)
        {
            var after = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.BuildFillScript(uri, values));
            using var refreshed = JsonDocument.Parse(after);
            outcome = outcome with { Unfilled = StringList(refreshed.RootElement, "unfilled") };
        }
        return outcome;
    }

    /// <summary>
    /// Works each React combobox the way a person does: focus it, type the answer so its list
    /// filters, wait for that list to render, then press Enter. The wait is why this cannot live
    /// inside the fill script — ExecuteScriptAsync returns immediately on a promise.
    /// </summary>
    private async Task<(int Filled, List<string> Touched)> FillCustomDropdownsAsync(
        IReadOnlyDictionary<string, string> values)
    {
        var touched = new List<string>();
        var planJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.BuildComboboxPlanScript(values));
        using var plan = JsonDocument.Parse(planJson);
        if (plan.RootElement.ValueKind != JsonValueKind.Array) return (0, touched);

        foreach (var entry in plan.RootElement.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var value = entry.GetProperty("value").GetString() ?? "";
            var label = entry.GetProperty("label").GetString() ?? "";
            if (value.Length == 0) continue;

            var typedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildComboboxTypeScript(index, value));
            using var typed = JsonDocument.Parse(typedJson);
            if (!typed.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) continue;

            await Task.Delay(450);
            var committedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildComboboxCommitScript(index));
            using var committed = JsonDocument.Parse(committedJson);
            if (committed.RootElement.TryGetProperty("ok", out var done) && done.GetBoolean())
                touched.Add(label.Length > 0 ? label : value);
        }
        return (touched.Count, touched);
    }

    private sealed record FillOutcome(
        string Adapter, int Filled, int Skipped,
        IReadOnlyList<string> Touched, IReadOnlyList<string> Unfilled);

    private static IReadOnlyList<string> StringList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>Names what the page still needs, so "review before submitting" says what to review.</summary>
    private static string DescribeUnfilled(IReadOnlyList<string> unfilled) =>
        unfilled.Count == 0
            ? ""
            : $"Still needs you ({unfilled.Count}): " + string.Join("; ", unfilled.Take(8)) +
              (unfilled.Count > 8 ? "; ..." : "");

    private async void OnFill(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            var result = await FillFieldsAsync(vm);
            var (_, advisory) = await DetectHumanGateAsync();
            vm.StatusMessage = $"{result.Adapter}: filled {result.Filled}, skipped {result.Skipped}. " +
                $"Review before submitting. {advisory} {DescribeUnfilled(result.Unfilled)}".Trim();
            if (TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out var uri))
            vm.RecordFill(uri.Host, result.Adapter, result.Filled, result.Skipped, result.Touched);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not fill fields: " + ex.Message;
            vm.RecordFailure("Field fill failed", ex.Message);
        }
    }

    private void OnChooseResume(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose the resume to upload",
            Filter = "Resume files (*.pdf;*.doc;*.docx)|*.pdf;*.doc;*.docx|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            vm.SelectedResumePath = Path.GetFullPath(dialog.FileName);
            vm.StatusMessage = "Resume selected.";
        }
    }

    private async void OnUploadResume(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try { await UploadResumeAsync(vm, reportStatus: true); }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not upload the resume: " + ex.Message;
            vm.RecordFailure("Resume upload failed", ex.Message);
        }
    }

    private async Task<bool> UploadResumeAsync(JobBrowserViewModel vm, bool reportStatus)
    {
        if (string.IsNullOrWhiteSpace(vm.SelectedResumePath) || !File.Exists(vm.SelectedResumePath))
        {
            if (reportStatus) vm.StatusMessage = "Choose an existing resume file first.";
            return false;
        }
        var extension = Path.GetExtension(vm.SelectedResumePath).ToLowerInvariant();
        if (extension is not (".pdf" or ".doc" or ".docx")) return false;
        var input = await FindResumeFileInputAsync();
        if (input == null)
        {
            if (reportStatus) vm.StatusMessage = "No resume input was found. Open the application form or upload manually.";
            return false;
        }
        var parameters = JsonSerializer.Serialize(new { files = new[] { vm.SelectedResumePath }, backendNodeId = input.Value.BackendNodeId });
        await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", parameters);
        await NotifyFileInputAsync(input.Value.BackendNodeId);
        var fileName = Path.GetFileName(vm.SelectedResumePath);
        if (TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out var uri)) vm.RecordUpload(uri.Host, fileName);
        if (reportStatus) vm.StatusMessage = $"Uploaded {fileName}. Confirm it on the page.";
        return true;
    }

    private async Task<(int BackendNodeId, int Score)?> FindResumeFileInputAsync()
    {
        await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
        var json = await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getFlattenedDocument", "{\"depth\":-1,\"pierce\":true}");
        using var document = JsonDocument.Parse(json);
        (int BackendNodeId, int Score)? best = null;
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("nodeName", out var nodeName) ||
                !string.Equals(nodeName.GetString(), "INPUT", StringComparison.OrdinalIgnoreCase) ||
                !node.TryGetProperty("attributes", out var attributes)) continue;
            var parts = attributes.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            var isFile = Enumerable.Range(0, parts.Length / 2).Any(i => parts[i * 2].Equals("type", StringComparison.OrdinalIgnoreCase) && parts[i * 2 + 1].Equals("file", StringComparison.OrdinalIgnoreCase));
            if (!isFile || !node.TryGetProperty("backendNodeId", out var backendNodeId)) continue;
            var description = string.Join(' ', parts).ToLowerInvariant();
            var score = (description.Contains("resume") ? 100 : 0) + (description.Contains("curriculum") || description.Contains("cv") ? 80 : 0)
                        - (description.Contains("cover") || description.Contains("portfolio") || description.Contains("photo") ? 100 : 0);
            var candidate = (BackendNodeId: backendNodeId.GetInt32(), Score: score);
            if (best == null || candidate.Score > best.Value.Score) best = candidate;
        }
        return best is { Score: >= 0 } ? best : null;
    }

    private async Task NotifyFileInputAsync(int backendNodeId)
    {
        var resolvedJson = await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.resolveNode", JsonSerializer.Serialize(new { backendNodeId }));
        using var resolved = JsonDocument.Parse(resolvedJson);
        if (!resolved.RootElement.GetProperty("object").TryGetProperty("objectId", out var objectId)) return;
        var parameters = JsonSerializer.Serialize(new
        {
            objectId = objectId.GetString(),
            functionDeclaration = "function(){this.dispatchEvent(new Event('input',{bubbles:true}));this.dispatchEvent(new Event('change',{bubbles:true}));}",
            returnByValue = true,
        });
        await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", parameters);
    }

    private static bool TryGetHttpUri(string? value, out Uri uri)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed != null &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
        uri = parsed ?? new Uri("about:blank");
        return valid;
    }
}
