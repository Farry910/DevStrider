using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DevStrider.Desktop.Views;

public partial class JobBrowserView : UserControl
{
    /// <summary>
    /// The browsers behind the tabs, one per open application, keyed by work item.
    ///
    /// <para>
    /// They all live in the same Grid cell and are all arranged at full size. Only the selected one
    /// is <see cref="Visibility.Visible"/>; the rest are <see cref="Visibility.Hidden"/>, never
    /// Collapsed, because a collapsed WebView2 is arranged at zero size and forwards that to the
    /// browser as the viewport — every offsetWidth test on the page then reports false and the
    /// filler skips every field. Hidden keeps a real viewport, which is what lets the run fill a tab
    /// while somebody reads another one.
    /// </para>
    /// </summary>
    private readonly Dictionary<Guid, WebView2> _browsers = new();

    private CoreWebView2Environment? _environment;

    /// <summary>The browser the run drives. Every script in this file goes to this one.</summary>
    private WebView2? _automationBrowser;

    /// <summary>
    /// Created before any work item exists, and adopted by the first one that arrives, so the very
    /// first job does not open a second browser next to an empty one.
    /// </summary>
    private WebView2? _unassignedBrowser;

    /// <summary>
    /// The browser the automation acts on.
    ///
    /// <para>
    /// This was a XAML-declared field, and stays a property with the same name so the ~80 call sites
    /// that drive the page did not have to learn about tabs. Which browser it resolves to is the
    /// only thing that changed.
    /// </para>
    /// </summary>
    /// <summary>
    /// Stands in when there is no browser yet, so callers see a null CoreWebView2 rather than an
    /// exception.
    ///
    /// <para>
    /// Most call sites here guard with <c>JobSiteBrowser.CoreWebView2 == null</c> and expect that to
    /// be answerable. Throwing from the property instead turned a quiet "not ready, come back later"
    /// into a failure the operator saw as "the job browser is unavailable" - which is what happened
    /// between disposing a finished tab and creating the next one. An uninitialised WebView2 costs
    /// nothing until EnsureCoreWebView2Async is called on it, and it makes those guards true again.
    /// </para>
    /// </summary>
    private WebView2? _placeholder;

    private WebView2 JobSiteBrowser =>
        _automationBrowser ?? _unassignedBrowser ?? (_placeholder ??= new WebView2());

    /// <summary>Builds a browser, wires it, and puts it in the host behind the tabs.</summary>
    private async Task<WebView2> CreateBrowserAsync()
    {
        _environment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevStrider", "webview2", "job-sites"));

        var browser = new WebView2 { Visibility = Visibility.Hidden };
        BrowserHost.Children.Add(browser);
        await browser.EnsureCoreWebView2Async(_environment);
        browser.CoreWebView2.SourceChanged += OnBrowserSourceChanged;
        browser.CoreWebView2.WebMessageReceived += OnJobSiteWebMessageReceived;
        return browser;
    }

    /// <summary>Gives the run a browser for this work item, adopting the spare one if it is free.</summary>
    private async Task OpenAutomationTabAsync(Guid workItemId)
    {
        try
        {
            if (_browsers.TryGetValue(workItemId, out var existing))
            {
                _automationBrowser = existing;
                ShowOnly(existing);
                return;
            }

            WebView2 browser;
            if (_unassignedBrowser != null)
            {
                browser = _unassignedBrowser;
                _unassignedBrowser = null;
            }
            else browser = await CreateBrowserAsync();

            _browsers[workItemId] = browser;
            _automationBrowser = browser;
            ShowOnly(browser);
        }
        catch (Exception ex) when (DataContext is JobBrowserViewModel vm)
        {
            vm.StatusMessage = "Could not open another application tab: " + ex.Message;
            vm.RecordFailure("Application tab", ex.Message);
        }
    }

    /// <summary>Disposes the browser behind a tab that has been dealt with.</summary>
    private void CloseTab(Guid workItemId)
    {
        if (!_browsers.Remove(workItemId, out var browser)) return;
        if (ReferenceEquals(_automationBrowser, browser)) _automationBrowser = null;
        try
        {
            if (browser.CoreWebView2 != null)
            {
                browser.CoreWebView2.SourceChanged -= OnBrowserSourceChanged;
                browser.CoreWebView2.WebMessageReceived -= OnJobSiteWebMessageReceived;
            }
            BrowserHost.Children.Remove(browser);
            browser.Dispose();
        }
        catch (Exception ex)
        {
            Trace?.Warn("Browser", "closing a tab did not go cleanly", ex.Message);
        }
    }

    /// <summary>Brings a tab to the front, leaving every other browser laid out but unpainted.</summary>
    private void SelectTab(ApplicationTabViewModel? tab)
    {
        if (tab == null || !_browsers.TryGetValue(tab.WorkItemId, out var browser)) return;
        ShowOnly(browser);
    }

    private void ShowOnly(WebView2 browser)
    {
        foreach (var child in BrowserHost.Children.OfType<WebView2>())
            child.Visibility = ReferenceEquals(child, browser) ? Visibility.Visible : Visibility.Hidden;
    }

    private void OnSelectTab(object sender, RoutedEventArgs e)
    {
        if (DataContext is JobBrowserViewModel vm && sender is FrameworkElement { Tag: ApplicationTabViewModel tab })
            vm.SelectedTab = tab;
    }

    private bool _initialized;
    private bool _validationRepairing;

    /// <summary>
    /// Fields this page has already been given a verified value for, keyed by normalised label.
    ///
    /// <para>
    /// A fill pass is not a one-shot: validation sends the run back for a correction pass, and an
    /// intermediate Next step starts another. Without a ledger every pass reconsiders the whole form,
    /// so a name typed and confirmed in pass one is selected, cleared and retyped in pass two for no
    /// reason — several seconds each, and a controlled input that drops the retyped value turns a
    /// correct field into an empty one. Entries are added only after the host verified the value
    /// stuck, and removed only when the form itself reports an error against that field.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _settledFields = new(StringComparer.Ordinal);

    /// <summary>The page the ledger describes. A different form is a different set of fields.</summary>
    private string _settledPage = "";

    /// <summary>Resolved from the DataContext rather than injected: the view is created by XAML.</summary>
    private BidTraceService? Trace => (DataContext as JobBrowserViewModel)?.Trace;

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
            // One browser up front so the address bar and the manual tools work before any queue
            // runs. The first work item adopts it rather than opening a second.
            _unassignedBrowser = await CreateBrowserAsync();
            _unassignedBrowser.Visibility = Visibility.Visible;
            if (DataContext is JobBrowserViewModel vm)
            {
                vm.QueueNavigationRequested += NavigateToQueuedLink;
                vm.ManualJobDescriptionRequested += OpenLinkForManualJobDescription;
                vm.ApplicationFillRequested += FillApplicationAutomatically;
                vm.ApplicationRefillRequested += RefillApplicationAutomatically;
                vm.AutomationTabRequested += OpenAutomationTabAsync;
                vm.TabCloseRequested += CloseTab;
                vm.TabSelectionChanged += SelectTab;
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

    /// <summary>
    /// One automatic bid, as a fixed sequence of steps with a checked shape between each pair.
    ///
    /// <para>
    /// The order matters and is not negotiable: read the description from the posting, then open the
    /// form, then read the questions. It used to read whichever page had loaded and then click
    /// through to the form, which on any site that keeps the posting and the form at two addresses
    /// meant the description was whatever the form page happened to say.
    /// </para>
    ///
    /// <para>
    /// A step that produces the wrong shape ends this link rather than passing the wrong shape on.
    /// The link is recorded with what was expected and what arrived, and the queue takes the next
    /// one - a bad page costs one entry, not the batch.
    /// </para>
    /// </summary>
    private async void NavigateToQueuedLink()
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null ||
            !TryGetHttpUri(vm.Address, out var uri)) return;
        var contract = new FlowContract(Trace);
        try
        {
            contract.Input("navigate", uri.Scheme is "http" or "https",
                "an absolute http(s) job URL", uri.AbsoluteUri);
            vm.AdapterName = JobSiteFormAdapters.NameFor(uri);

            // Ashby and Lever serve the posting and the form at two addresses, and an aggregator
            // link points at the form. Go to the posting for the description and come back.
            var posting = JobPostingExtractor.PostingUrlFor(uri);
            if (!JobPostingExtractor.SamePage(posting, uri))
                Trace?.Step("Browser", "description lives on the posting page", posting.AbsoluteUri);
            Trace?.Step("Browser", "navigating", $"{posting.AbsoluteUri} (adapter {vm.AdapterName})");
            await NavigateAsync(posting);
            contract.Output("navigate", TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out _),
                "a loaded http(s) page", JobSiteBrowser.CoreWebView2?.Source ?? "nothing");
            Trace?.Ok("Browser", "navigation complete", JobSiteBrowser.CoreWebView2?.Source ?? "");
            if (!vm.IsAutomaticQueueRunning)
            {
                Trace?.Warn("Browser", "queue not running", "stopping after navigation");
                return;
            }
            await Task.Delay(900);
            vm.BeginPageExtraction();

            var gate = await CheckHumanGateAsync();
            if (!string.IsNullOrWhiteSpace(gate))
            {
                if (vm.CurrentQueueItem != null)
                    await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, gate);
                return;
            }

            var jobDescription = await ReadJobDescriptionAsync(contract);

            gate = await OpenApplicationFormAsync(contract, uri, posting);
            if (!string.IsNullOrWhiteSpace(gate))
            {
                if (vm.CurrentQueueItem != null)
                    await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, gate);
                return;
            }

            var questions = await ReadQuestionsAsync(contract);
            await vm.AcceptExtractedPageAsync(jobDescription, questions);
        }
        catch (FlowFormatException ex) when (ex.Step == "job description")
        {
            // A description a person can point at in seconds is not worth failing a link over, and
            // guessing at it wrecks everything downstream. Set it aside and keep the batch moving.
            if (vm.CurrentQueueItem != null)
                await vm.DeferForManualJobDescriptionAsync(vm.CurrentQueueItem.Id, ex.Actual);
        }
        catch (FlowFormatException ex)
        {
            // Every other step ran and produced something unusable. Nothing downstream can recover
            // from that, so this link is done and the next one starts.
            Trace?.Warn("Run", "abandoning link on a format violation", ex.Summary);
            if (vm.CurrentQueueItem != null)
                await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, ex.Summary);
        }
        catch (Exception ex)
        {
            if (vm.CurrentQueueItem != null)
                await vm.MarkAutomationFailureAsync(vm.CurrentQueueItem.Id, "Could not read the job page: " + ex.Message);
        }
    }


    /// <summary>
    /// Opens a link the automatic pass set aside, and stops. No extraction runs: the adapters
    /// already looked, and looking again would only produce the same wrong answer.
    /// </summary>
    private async void OpenLinkForManualJobDescription()
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null ||
            !TryGetHttpUri(vm.Address, out var uri)) return;
        try
        {
            // Land on the posting rather than the form: it is the page the description is on, and
            // the one a person can read it from.
            var posting = JobPostingExtractor.PostingUrlFor(uri);
            Trace?.Step("Browser", "opening for a manual description", posting.AbsoluteUri);
            await NavigateAsync(posting);
            await Task.Delay(900);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not open that link: " + ex.Message;
            vm.RecordFailure("Manual description pass", $"{vm.Address}: {ex.Message}");
        }
    }

    /// <summary>Takes whatever the person highlighted on the page as the job description.</summary>
    private async void OnUseSelectedJobDescription(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            var raw = await JobSiteBrowser.ExecuteScriptAsync(
                "(() => { const s = window.getSelection(); return s ? s.toString() : ''; })()");
            var selected = JsonSerializer.Deserialize<string>(raw) ?? "";
            Trace?.Step("Extract", "selection read", $"{selected.Trim().Length} chars");
            if (string.IsNullOrWhiteSpace(selected))
            {
                vm.StatusMessage = "Nothing is selected on the page. Highlight the job description first.";
                return;
            }
            await ContinueFromManualJobDescriptionAsync(vm, selected);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not read the selection: " + ex.Message;
        }
    }

    /// <summary>Takes the pasted text as the job description.</summary>
    private async void OnUsePastedJobDescription(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.FallbackJobDescription))
        {
            vm.StatusMessage = "Paste the job description first.";
            return;
        }
        try
        {
            await ContinueFromManualJobDescriptionAsync(vm, vm.FallbackJobDescription);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Could not continue: " + ex.Message;
        }
    }

    /// <summary>
    /// Rejoins the automatic flow with a description a person supplied: check it, open the form,
    /// read the questions, and hand over to resume generation exactly as the automatic pass does.
    /// </summary>
    private async Task ContinueFromManualJobDescriptionAsync(JobBrowserViewModel vm, string text)
    {
        // Held to the same standard as an extracted one, minus the container test there is no
        // container for. Somebody selecting the wrong half of the page should hear about it now.
        var rejection = JobPostingExtractor.RejectSupplied(text);
        if (rejection.Length > 0)
        {
            vm.StatusMessage = "That does not look like a job description: " + rejection;
            Trace?.Warn("Extract", "supplied description refused", rejection);
            return;
        }

        var contract = new FlowContract(Trace);
        vm.StatusMessage = "Description accepted. Opening the application form...";
        if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var current))
            throw new InvalidOperationException("The application URL is unavailable.");

        // The queue item names the form; the browser is on the posting. Where they differ, the
        // queued address is the one that opens the form.
        var target = TryGetHttpUri(vm.CurrentQueueItem?.Url, out var queued) ? queued : current;
        var gate = await OpenApplicationFormAsync(contract, target, current);
        if (!string.IsNullOrWhiteSpace(gate))
        {
            vm.StatusMessage = gate;
            return;
        }

        var questions = await ReadQuestionsAsync(contract);
        await vm.AcceptManualJobDescriptionAsync(text, questions);
    }
    /// <summary>The blocker text when a human is required, or an empty string when the page is ours.</summary>
    private async Task<string> CheckHumanGateAsync()
    {
        var (gate, advisory) = await DetectHumanGateAsync();
        Trace?.Step("Extract", "human gate checked",
            $"blocker={(gate.Length == 0 ? "none" : gate)}; advisory={(advisory.Length == 0 ? "none" : advisory)}");
        return gate;
    }

    /// <summary>
    /// Reads the description from the page that is open, and refuses anything that is not one.
    ///
    /// <para>
    /// The old read was <c>document.body.innerText</c> against a 180-character floor. Absci's
    /// application page clears that floor with 2,857 characters of contact fields, demographic
    /// survey and cookie banner, and every one of those characters went to ChatGPT as the job
    /// description. Structure is what separates them: a description is prose in a container with no
    /// form controls in it.
    /// </para>
    /// </summary>
    private async Task<string> ReadJobDescriptionAsync(FlowContract contract)
    {
        var raw = await JobSiteBrowser.ExecuteScriptAsync(JobPostingExtractor.ExtractScript);
        using var document = contract.Json("job description", raw, JsonValueKind.Object,
            "an object holding the description container");
        var read = JobPostingExtractor.Read(document.RootElement);
        Trace?.Step("Extract", "description read",
            $"{read.Text.Length} chars via {read.Source}; {read.Sentences} sentence(s), {read.Controls} form control(s)");
        Trace?.Payload("Extract", "description", read.Text);

        var rejection = JobPostingExtractor.Reject(read);
        contract.Output("job description", rejection.Length == 0,
            "prose from the posting with no form controls in it",
            rejection.Length == 0 ? "a job description" : rejection);
        return read.Text.Trim();
    }

    /// <summary>
    /// Puts the application form on screen, by URL where the site has one and by clicking Apply
    /// where it does not, and confirms a form actually arrived.
    /// </summary>
    private async Task<string> OpenApplicationFormAsync(FlowContract contract, Uri queued, Uri posting)
    {
        if (!JobPostingExtractor.SamePage(posting, queued))
        {
            // The queued link already named the form. Navigating there beats hunting for a button.
            Trace?.Step("Extract", "opening the application by URL", queued.AbsoluteUri);
            await NavigateAsync(queued);
            await Task.Delay(1200);
        }
        else if (TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var current))
        {
            var openResult = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteApplyAdapters.BuildOpenApplicationScript(current));
            Trace?.Step("Extract", "open-application script", openResult);
            using var opened = contract.Json("open application", openResult, JsonValueKind.Object,
                "an object reporting whether the apply control was clicked");
            if (opened.RootElement.TryGetProperty("clicked", out var clicked) && clicked.GetBoolean())
                await Task.Delay(1500);
        }

        var gate = await CheckHumanGateAsync();
        if (!string.IsNullOrWhiteSpace(gate)) return gate;

        // Give a slow form time to render before deciding it never arrived. Abandoning a good link
        // because a React form took another second is the expensive mistake here, not the wait.
        var controls = 0;
        for (var attempt = 0; attempt < 6 && controls < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(500);
            var formJson = await JobSiteBrowser.ExecuteScriptAsync(FormPresenceScript);
            using var form = contract.Json("open application", formJson, JsonValueKind.Object,
                "an object counting the controls on the page");
            controls = form.RootElement.TryGetProperty("controls", out var count) ? count.GetInt32() : 0;
        }
        Trace?.Step("Extract", "application form on screen", $"{controls} fillable control(s)");
        contract.Output("open application", controls >= 3,
            "an application form with at least 3 fillable controls", $"{controls} control(s)");
        return "";
    }

    /// <summary>Counts what a form would have, so "the form opened" is checked and not assumed.</summary>
    private const string FormPresenceScript = """
(() => {
  const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
  const controls = Array.from(document.querySelectorAll(
    'input:not([type="hidden"]),select,textarea,[role="radio"],[role="checkbox"],[role="combobox"]'))
    .filter(visible);
  return { controls: controls.length, files: controls.filter(e => e.type === 'file').length };
})()
""";

    /// <summary>Reads the questions the form asks, and checks the result is a list this run can act on.</summary>
    private async Task<string> ReadQuestionsAsync(FlowContract contract)
    {
        var questions = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
        contract.Json("questions", questions, JsonValueKind.Array, "an array of questions").Dispose();
        questions = await EnrichDropdownQuestionsAsync(questions);
        using var enriched = contract.Json("questions", questions, JsonValueKind.Array,
            "an array of questions with dropdown options merged in");
        var total = enriched.RootElement.GetArrayLength();
        var named = enriched.RootElement.EnumerateArray().Count(item =>
            item.TryGetProperty("question", out var text) && !string.IsNullOrWhiteSpace(text.GetString()));
        Trace?.Step("Extract", "questions read", $"{total} entr(ies), {named} with a question");
        Trace?.Payload("Extract", "questions with dropdown options", questions);
        contract.Output("questions", named == total,
            "every entry to carry a question", $"{named} of {total} named");
        return questions;
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
        // Every tab raises this. Only the one being driven owns the address bar and the adapter
        // name; a reviewer clicking a link in a parked tab must not redirect the run.
        if (DataContext is not JobBrowserViewModel vm || !IsAutomationSender(sender) ||
            !Uri.TryCreate(_automationBrowser?.CoreWebView2?.Source, UriKind.Absolute, out var uri)) return;
        vm.Address = uri.AbsoluteUri;
        vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
    }

    /// <summary>True when an event came from the browser the run is driving.</summary>
    private bool IsAutomationSender(object? sender) =>
        _automationBrowser?.CoreWebView2 != null && ReferenceEquals(sender, _automationBrowser.CoreWebView2);

    /// <summary>The tab an event came from, or null when the browser is no longer tracked.</summary>
    private ApplicationTabViewModel? TabForSender(JobBrowserViewModel vm, object? sender)
    {
        var match = _browsers.FirstOrDefault(pair => ReferenceEquals(sender, pair.Value.CoreWebView2));
        return match.Value == null ? null : vm.Tabs.FirstOrDefault(tab => tab.WorkItemId == match.Key);
    }

    private async void OnJobSiteWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_validationRepairing || DataContext is not JobBrowserViewModel vm) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                type.GetString() != "devstrider-submit-validation" ||
                !root.TryGetProperty("errors", out var errorsElement) ||
                errorsElement.ValueKind != JsonValueKind.Array) return;
            var errors = ValidationErrors(root);

            // The message can come from any open tab, and the recovery below drives the page through
            // JobSiteBrowser - the tab the run is on. Firing it for a parked tab would fill the wrong
            // application, and would want ChatGPT while the run is already using it. A parked tab
            // gets its errors written onto the tab instead, for the person already looking at it.
            if (!IsAutomationSender(sender))
            {
                var source = TabForSender(vm, sender);
                if (source != null && errors.Count > 0)
                {
                    source.Status = "Site rejected the submission";
                    source.Summary = "The site returned: " + string.Join("; ", errors.Take(6)
                        .Select(error => error.Question == error.Message
                            ? error.Message
                            : $"{error.Question}: {error.Message}"));
                    Trace?.Warn("Review", "a parked tab was rejected on submit", source.Summary);
                }
                return;
            }
            if (!vm.IsReadyForReview) return;
            if (errors.Count == 0) return;
            _validationRepairing = true;
            var descriptions = errors.Select(error => error.Question == error.Message
                    ? error.Message
                    : $"{error.Question}: {error.Message}")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await vm.BeginValidationRepairAsync(descriptions);

            // Preserve the site's own rejection as the reason for the second answer. Retyping the
            // old answer first used to erase the rendered errors and ask ChatGPT nothing useful.
            var correctionQuestions = await BuildCorrectionQuestionsAsync(vm, [], errors);
            if (await vm.RequestAnswerCorrectionAsync(correctionQuestions)) return;

            // If the one correction pass was already consumed or no error could be mapped, make a
            // best-effort refill and return to review with the original site errors still recorded.
            var fill = await FillFieldsAsync(vm, errors.Select(error => error.Question).ToArray());
            var validation = await ValidateAndAdvanceAsync(vm, fill);
            fill = validation.Fill;
            if (validation.Submitted)
            {
                await vm.MarkSubmittedAutomaticallyAsync(validation.Note);
                return;
            }
            await InstallHumanSubmitObserverAsync();
            var note = "Validation errors appeared after Submit: " + string.Join("; ", descriptions.Take(8)) +
                       (string.IsNullOrWhiteSpace(validation.Note) ? "" : " " + validation.Note);
            await vm.MarkReadyForReviewAsync(fill.Adapter, fill.Filled, fill.Skipped, fill.Touched,
                !string.IsNullOrWhiteSpace(vm.SelectedResumePath), note, fill.Unfilled);
        }
        catch (Exception ex)
        {
            vm.RecordFailure("Submit validation recovery failed", ex.Message);
            vm.StatusMessage = "Could not recover the validation errors automatically: " + ex.Message;
        }
        finally { _validationRepairing = false; }
    }

    /// <summary>
    /// The manual "start application" read: the same steps as the automatic flow, run against
    /// whichever page the person is already looking at.
    ///
    /// <para>
    /// One difference, and it is deliberate. A description that fails its format check leaves the
    /// field empty so the person can paste one in, instead of ending anything. The automatic flow
    /// gives up on the link because nobody is watching it; here somebody is.
    /// </para>
    /// </summary>
    private async Task<(string JobDescription, string QuestionsJson, string Gate)> ExtractPageAsync(bool openApplication)
    {
        var contract = new FlowContract(Trace);
        var gate = await CheckHumanGateAsync();
        if (!string.IsNullOrWhiteSpace(gate)) return ("", "[]", gate);

        var jobDescription = "";
        try
        {
            jobDescription = await ReadJobDescriptionAsync(contract);
        }
        catch (FlowFormatException ex)
        {
            Trace?.Warn("Extract", "no usable description on this page", ex.Summary);
        }

        if (openApplication)
        {
            if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var current))
                throw new InvalidOperationException("The application URL is unavailable.");
            gate = await OpenApplicationFormAsync(contract, current, current);
            if (!string.IsNullOrWhiteSpace(gate)) return (jobDescription, "[]", gate);
        }

        var questions = await ReadQuestionsAsync(contract);
        return (jobDescription, questions, "");
    }

    /// <summary>
    /// Opens each unanswered custom dropdown before ChatGPT is called, walks its scroll viewport so
    /// virtualized options are materialized, and merges every discovered option into that question.
    /// No option is selected during discovery.
    /// </summary>
    private async Task<string> EnrichDropdownQuestionsAsync(string questionsJson)
    {
        var discovered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Ashby exposes the complete static select schema to its own public application page. Read
        // it before touching the UI because these choices exist even when the menu has no ARIA roles.
        var schemaStartJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.StartAshbyDropdownSchemaScript);
        using (var schemaStart = JsonDocument.Parse(schemaStartJson))
        {
            if (schemaStart.RootElement.TryGetProperty("started", out var started) && started.GetBoolean())
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    await Task.Delay(100);
                    var schemaJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.ReadAshbyDropdownSchemaScript);
                    using var schema = JsonDocument.Parse(schemaJson);
                    var status = schema.RootElement.TryGetProperty("status", out var statusElement)
                        ? statusElement.GetString() ?? ""
                        : "";
                    if (status == "loading") continue;
                    if (status == "ready" && schema.RootElement.TryGetProperty("questions", out var schemaQuestions))
                    {
                        foreach (var item in schemaQuestions.EnumerateArray())
                        {
                            var question = item.GetProperty("question").GetString() ?? "";
                            var options = StringList(item, "options").Distinct(StringComparer.Ordinal).ToList();
                            if (question.Length > 0 && options.Count > 0) discovered[question] = options;
                        }
                    }
                    else if (status == "error")
                    {
                        var error = schema.RootElement.TryGetProperty("error", out var errorElement)
                            ? errorElement.GetString() ?? "unknown error"
                            : "unknown error";
                        Trace?.Warn("Extract", "Ashby dropdown schema unavailable", error);
                    }
                    break;
                }
            }
        }
        if (discovered.Count > 0)
            Trace?.Step("Extract", "Ashby dropdown schema loaded",
                string.Join("; ", discovered.Select(pair => $"{pair.Key}={pair.Value.Count}")));

        var candidateValues = (DataContext as JobBrowserViewModel)?.BuildFillValues()
            ?? new Dictionary<string, string>();
        var planJson = await JobSiteBrowser.ExecuteScriptAsync(
            JobSiteFormAdapters.BuildDropdownQuestionPlanScript(candidateValues));
        Trace?.Payload("Extract", "dropdown option plan", planJson);
        using var plan = JsonDocument.Parse(planJson);
        if (plan.RootElement.ValueKind != JsonValueKind.Array) return questionsJson;

        foreach (var entry in plan.RootElement.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var question = entry.GetProperty("question").GetString() ?? "";
            var candidate = entry.TryGetProperty("candidate", out var candidateElement)
                ? candidateElement.GetString() ?? ""
                : "";
            if (question.Length == 0) continue;
            if (discovered.ContainsKey(question)) continue;

            var openedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildDropdownQuestionOpenScript(index));
            using var opened = JsonDocument.Parse(openedJson);
            if (!opened.RootElement.TryGetProperty("ok", out var openOk) || !openOk.GetBoolean())
            {
                Trace?.Warn("Extract", $"could not open dropdown \"{question}\"", openedJson);
                continue;
            }

            // Read the complete menu produced by pointer/mousedown/focus before filtering it. The
            // old order typed the candidate immediately and therefore sent ChatGPT only the small
            // filtered subset, even though focusing the control exposed every valid choice.
            await Task.Delay(100);
            var options = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reachedEnd = false;
            var stabilized = false;
            for (var phase = 0; phase < 2; phase++)
            {
                reachedEnd = false;
                var emptyPolls = 0;
                var stableReads = 0;
                for (var page = 0; page < 500; page++)
                {
                    var beforeCount = options.Count;
                    var readJson = await JobSiteBrowser.ExecuteScriptAsync(
                        JobSiteFormAdapters.BuildDropdownQuestionReadScript(index));
                    using var read = JsonDocument.Parse(readJson);
                    if (read.RootElement.TryGetProperty("options", out var optionArray) &&
                        optionArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var option in optionArray.EnumerateArray())
                        {
                            var text = option.GetString()?.Trim() ?? "";
                            if (text.Length > 0 && seen.Add(text)) options.Add(text);
                        }
                    }

                    var advanced = read.RootElement.TryGetProperty("advanced", out var advancedElement) &&
                                   advancedElement.GetBoolean();
                    stableReads = options.Count == beforeCount && !advanced ? stableReads + 1 : 0;

                    reachedEnd = read.RootElement.TryGetProperty("atEnd", out var end) && end.GetBoolean();
                    // React menus commonly mount on a later render after the click task returns.
                    // Do not mistake the first empty DOM snapshot for a dropdown with no options.
                    if (options.Count == 0 && emptyPolls++ < 12)
                    {
                        await Task.Delay(125);
                        continue;
                    }
                    if (reachedEnd) break;
                    // Some Greenhouse menus report a scroll range whose scrollTop never advances.
                    // Once the option set has stopped changing across several reads, it is done for
                    // practical purposes; waiting 500 iterations made a two-choice menu hang until
                    // the user clicked elsewhere and destroyed it.
                    //
                    // An empty list counts as stopped too. A search-first menu — Greenhouse's
                    // Location (City) is one — renders nothing at all until a term is typed, and
                    // requiring at least one option here meant the whole 500 × 80ms was spent
                    // waiting for options that only appear after the search phase below: 40 seconds
                    // of an extraction that otherwise takes twelve, once per such field.
                    if (stableReads >= 8)
                    {
                        stabilized = options.Count > 0;
                        break;
                    }
                    await Task.Delay(80);
                }

                if (options.Count > 0 || phase > 0 || candidate.Length == 0) break;
                var searchJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildDropdownQuestionSearchScript(index, candidate));
                Trace?.Step("Extract", $"searched dropdown \"{question}\"", $"{candidate} -> {searchJson}");
                await Task.Delay(400);
            }

            await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.BuildDropdownQuestionCloseScript(index));
            if (options.Count > 0) discovered[question] = options;
            Trace?.Step("Extract", $"dropdown options for \"{question}\"",
                $"{options.Count} option(s), complete={reachedEnd}, stable={stabilized}: {string.Join(" | ", options.Take(20))}" +
                (options.Count > 20 ? " | ..." : ""));
        }

        if (discovered.Count == 0) return questionsJson;
        var root = JsonNode.Parse(questionsJson) as JsonArray;
        if (root == null) return questionsJson;
        foreach (var node in root.OfType<JsonObject>())
        {
            var question = node["question"]?.GetValue<string>() ?? "";
            if (!discovered.TryGetValue(question, out var options)) continue;
            var optionArray = new JsonArray();
            foreach (var option in options) optionArray.Add(option);
            node["options"] = optionArray;
            node.Remove("type");
        }
        return root.ToJsonString();
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
            var questions = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
            vm.FormQuestionsJson = await EnrichDropdownQuestionsAsync(questions);
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
        var contract = new FlowContract(Trace);
        try
        {
            if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out _) && TryGetHttpUri(result.JobUrl, out var target))
                await NavigateAsync(target);
            contract.Input("fill", TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out _),
                "an application page open in the browser", JobSiteBrowser.CoreWebView2.Source ?? "nothing");

            // The shell has just brought this workspace forward. Let the layout pass finish so the
            // WebView is at its final size before any script measures the page.
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            var uploaded = false;
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.ResumeFilePath) && File.Exists(result.ResumeFilePath))
            {
                // The macro can leave a file behind that exists and is not a document - a stub Word
                // wrote before it failed, or a PDF export that produced nothing. Uploading that is
                // worse than not applying, because the application looks complete and is not.
                var rejection = RejectResumeFile(result.ResumeFilePath);
                contract.Output("resume document", rejection.Length == 0, "a readable PDF",
                    rejection.Length == 0 ? Path.GetFileName(result.ResumeFilePath) : rejection);
                vm.SelectedResumePath = result.ResumeFilePath;
                uploaded = await UploadResumeAsync(vm, reportStatus: false);
                // Ashby can rerender its controlled form after a file input changes. Upload first,
                // then type into the final DOM instead of letting upload erase completed answers.
                if (uploaded) await Task.Delay(750);
            }
            else notes.Add("No generated resume file was found; choose it manually.");

            // This handler only ever runs the first fill, straight off resume generation. The
            // correction round arrives on ApplicationRefillRequested instead, and already commits
            // the form before it retypes anything — see RefillApplicationAutomatically.
            var fill = await FillFieldsAsync(vm);
            // Nothing verified on a first pass means the page is not the shape the adapters read,
            // and another pass would not change that - unless nothing needed filling, which is a
            // complete form rather than a broken one.
            contract.Output("fill", fill.Touched.Count > 0 || fill.Unfilled.Count == 0,
                "at least one field filled and read back, or nothing left to fill",
                $"{fill.Touched.Count} verified, {fill.Skipped} skipped, {fill.Unfilled.Count} outstanding");
            var validation = await ValidateAndAdvanceAsync(vm, fill);
            fill = validation.Fill;
            if (!string.IsNullOrWhiteSpace(validation.Note)) notes.Add(validation.Note);
            if (validation.Submitted)
            {
                await vm.MarkSubmittedAutomaticallyAsync(validation.Note);
                return;
            }

            // The site s own errors are the best evidence of what is wrong, and when there are any
            // they are used on their own. But Submit can produce neither a confirmation nor an error
            // - the click lands, the page does not move - and the run was then handing a form with
            // known-empty fields straight to a person without ever trying to finish it. In that case
            // the empty fields are the only evidence left, so they drive the second pass. They are
            // matched against the question inventory on the way through, so protected and demographic
            // fields, which have no question entry, cannot reach ChatGPT this way.
            var correctionQuestions = validation.Errors.Count > 0
                ? await BuildCorrectionQuestionsAsync(vm, [], validation.Errors)
                : fill.Unfilled.Count > 0
                    ? await BuildCorrectionQuestionsAsync(vm, fill.Unfilled, [])
                    : "[]";
            if (validation.Errors.Count == 0 && fill.Unfilled.Count > 0)
                Trace?.Step("Fill", "submit gave no verdict; second pass driven by empty fields",
                    string.Join(" | ", fill.Unfilled.Take(10)));
            if (await vm.RequestAnswerCorrectionAsync(correctionQuestions)) return;

            // A challenge that appears only once the form is filled is still the human's to solve at
            // submit time, so it belongs in the review note rather than failing a filled application.
            var (_, advisory) = await DetectHumanGateAsync();
            if (!string.IsNullOrWhiteSpace(advisory)) notes.Add(advisory);
            var outstanding = DescribeUnfilled(fill.Unfilled);
            if (!string.IsNullOrWhiteSpace(outstanding)) notes.Add(outstanding);

            await vm.MarkReadyForReviewAsync(fill.Adapter, fill.Filled, fill.Skipped, fill.Touched, uploaded,
                string.Join(" ", notes), fill.Unfilled);
        }
        catch (FlowFormatException ex)
        {
            Trace?.Warn("Run", "abandoning link on a format violation", ex.Summary);
            await vm.MarkAutomationFailureAsync(result.WorkItemId, ex.Summary);
        }
        catch (Exception ex)
        {
            await vm.MarkAutomationFailureAsync(result.WorkItemId, "Could not fill the application: " + ex.Message);
        }
    }

    private async void RefillApplicationAutomatically(Guid workItemId)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        if (vm.CurrentQueueItem?.Id != workItemId)
        {
            // The refill is the last step of the correction round. Dropping it silently because the
            // queue moved on leaves the corrected answers unapplied with nothing said about it.
            Trace?.Warn("Fill", "refill skipped",
                $"correction returned for {workItemId:N}, but the queue is on " +
                (vm.CurrentQueueItem == null ? "no item" : vm.CurrentQueueItem.Id.ToString("N")));
            vm.StatusMessage = "Corrected answers arrived after the queue moved on; that link needs a retry.";
            return;
        }
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            var forceLabels = CorrectionQuestionLabels(vm.CurrentQueueItem.PendingCorrectionQuestionsJson);
            Trace?.Step("Fill", "forced correction fields", string.Join(" | ", forceLabels));
            var fill = await FillFieldsAsync(vm, forceLabels);
            await vm.CompleteAnswerCorrectionRefillAsync(workItemId);
            var validation = await ValidateAndAdvanceAsync(vm, fill);
            fill = validation.Fill;
            if (validation.Submitted)
            {
                await vm.MarkSubmittedAutomaticallyAsync(validation.Note);
                return;
            }
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(validation.Note)) notes.Add(validation.Note);
            var (_, advisory) = await DetectHumanGateAsync();
            if (!string.IsNullOrWhiteSpace(advisory)) notes.Add(advisory);
            var outstanding = DescribeUnfilled(fill.Unfilled);
            if (!string.IsNullOrWhiteSpace(outstanding)) notes.Add(outstanding);
            await vm.MarkReadyForReviewAsync(fill.Adapter, fill.Filled, fill.Skipped, fill.Touched,
                !string.IsNullOrWhiteSpace(vm.SelectedResumePath), string.Join(" ", notes), fill.Unfilled);
        }
        catch (FlowFormatException ex)
        {
            Trace?.Warn("Run", "abandoning link on a format violation", ex.Summary);
            await vm.MarkAutomationFailureAsync(workItemId, ex.Summary);
        }
        catch (Exception ex)
        {
            await vm.MarkAutomationFailureAsync(workItemId,
                "Could not refill the corrected application fields: " + ex.Message);
        }
    }

    private async Task<string> BuildCorrectionQuestionsAsync(JobBrowserViewModel vm,
        IReadOnlyList<string> unfilled,
        IReadOnlyList<ValidationError> validationErrors)
    {
        if (unfilled.Count == 0 && validationErrors.Count == 0) return "[]";
        var unresolved = unfilled.Select(RemoveDropdownSuffix)
            .Where(label => NormalizeFieldLabel(label).Length > 0).ToArray();

        // Merge the original question inventory with the currently visible step. A site can reveal
        // new fields after Next, and Submit errors can refer to those even though they did not exist
        // when the resume/answer request started.
        var combined = JsonNode.Parse(vm.FormQuestionsJson) as JsonArray ?? new JsonArray();
        var seen = combined.OfType<JsonObject>()
            .Select(node => NormalizeFieldLabel(node["question"]?.GetValue<string>() ?? ""))
            .Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        var currentJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
        if (JsonNode.Parse(currentJson) is JsonArray current)
        {
            foreach (var node in current.OfType<JsonObject>())
            {
                var key = NormalizeFieldLabel(node["question"]?.GetValue<string>() ?? "");
                if (key.Length > 0 && seen.Add(key))
                    combined.Add(JsonNode.Parse(node.ToJsonString()));
            }
        }

        var enriched = await EnrichDropdownQuestionsAsync(combined.ToJsonString());
        var source = JsonNode.Parse(enriched) as JsonArray;
        if (source == null) return "[]";
        var known = vm.BuildFillValues();
        var retry = new JsonArray();
        var matchedErrors = new HashSet<ValidationError>();
        foreach (var node in source.OfType<JsonObject>())
        {
            var question = node["question"]?.GetValue<string>()?.Trim() ?? "";
            var fieldErrors = validationErrors.Where(error =>
                    FieldReferenceMatches(question, error.Question) ||
                    FieldReferenceMatches(question, error.Message))
                .ToArray();
            var missing = unresolved.Any(field => FieldReferenceMatches(question, field));
            if (!missing && fieldErrors.Length == 0) continue;
            foreach (var error in fieldErrors) matchedErrors.Add(error);

            var clone = JsonNode.Parse(node.ToJsonString())!.AsObject();
            var primary = known.FirstOrDefault(pair => FieldReferenceMatches(question, pair.Key)).Value ?? "";
            clone["primaryAnswer"] = primary;
            var messages = fieldErrors.Select(error => error.Message).Where(message => message.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            clone["failure"] = messages.Length > 0
                ? string.Join("; ", messages)
                : "The primary answer was missing or the form did not retain it as valid.";
            if (fieldErrors.Length > 0)
            {
                var errorArray = new JsonArray();
                foreach (var error in fieldErrors)
                    errorArray.Add(new JsonObject { ["question"] = error.Question, ["message"] = error.Message });
                clone["validationErrors"] = errorArray;
            }
            retry.Add(clone);
        }

        // Usually the field association above succeeds. Preserve an unmatched structured site error
        // anyway, because it is better context than silently asking no second-pass question at all.
        foreach (var error in validationErrors.Where(error => !matchedErrors.Contains(error)))
        {
            if (error.Question.Length == 0 || error.Question.Equals("Required field", StringComparison.OrdinalIgnoreCase))
                continue;
            var primary = known.FirstOrDefault(pair => FieldReferenceMatches(error.Question, pair.Key)).Value ?? "";
            retry.Add(new JsonObject
            {
                ["question"] = error.Question,
                ["primaryAnswer"] = primary,
                ["failure"] = error.Message,
                ["validationErrors"] = new JsonArray(new JsonObject
                    { ["question"] = error.Question, ["message"] = error.Message })
            });
        }
        return retry.ToJsonString();
    }

    private static bool FieldReferenceMatches(string question, string reference)
    {
        var field = NormalizeFieldLabel(RemoveDropdownSuffix(question));
        var candidate = NormalizeFieldLabel(RemoveDropdownSuffix(reference));
        if (field.Length == 0 || candidate.Length == 0) return false;
        if (field == candidate) return true;
        // Validation messages often wrap the label: "Missing entry for required field: Email".
        // Keep the minimum long enough that a generic "Name" cannot match "Company name".
        return field.Length >= 5 && candidate.Contains(field, StringComparison.Ordinal) ||
               candidate.Length >= 8 && field.Contains(candidate, StringComparison.Ordinal);
    }

    private static string RemoveDropdownSuffix(string value) =>
        value.Replace(" (dropdown)", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static IReadOnlyList<string> CorrectionQuestionLabels(string questionsJson)
    {
        try
        {
            return (JsonNode.Parse(questionsJson) as JsonArray)?.OfType<JsonObject>()
                .Select(node => node["question"]?.GetValue<string>()?.Trim() ?? "")
                .Where(label => label.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Validates the visible step with browser-level input, advances intermediate actions, and
    /// clicks final Submit after the primary/correction fill. Site errors become second-pass input;
    /// a successful submission completes the queue item.
    /// </summary>
    private async Task<ApplicationValidationOutcome> ValidateAndAdvanceAsync(
        JobBrowserViewModel vm, FillOutcome initial)
    {
        var aggregate = initial;
        var notes = new List<string>();
        for (var step = 0; step < 5; step++)
        {
            if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var uri)) break;
            var script = JobSiteApplyAdapters.BuildValidationScript(uri, allowSafeAdvance: true);
            var json = await JobSiteBrowser.ExecuteScriptAsync(script);
            Trace?.Step("Validate", $"{JobSiteApplyAdapters.Resolve(uri).Name} step {step + 1}", json);
            using var result = new FlowContract(Trace).Json("validate", json, JsonValueKind.Object,
                "an object naming the next action and any errors");
            var root = result.RootElement;
            var errors = ValidationErrors(root);
            if (errors.Count > 0)
            {
                UnsettleFields(errors.Select(error => error.Question));
                aggregate = aggregate with
                {
                    Unfilled = aggregate.Unfilled.Concat(errors.Select(error => error.Question))
                        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
                notes.Add("Adapter validation found: " + string.Join("; ", errors.Take(8)
                    .Select(error => error.Question == error.Message
                        ? error.Message
                        : $"{error.Question}: {error.Message}")));
                return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes), errors, false);
            }

            var action = root.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString() ?? "none"
                : "none";
            if (action == "success")
                return new ApplicationValidationOutcome(aggregate, "Application submission confirmed.",
                    Array.Empty<ValidationError>(), true);

            if (action == "next" && TryCoordinates(root, out var nextX, out var nextY))
            {
                var sourceBeforeNext = JobSiteBrowser.CoreWebView2?.Source ?? "";
                await DispatchBrowserMouseClickAsync(nextX, nextY);
                notes.Add("Advanced an intermediate Next/Continue step.");
                IReadOnlyList<ValidationError> nextErrors = Array.Empty<ValidationError>();
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 700 : 250);
                    if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var probeUri)) continue;
                    var probeJson = await JobSiteBrowser.ExecuteScriptAsync(
                        JobSiteApplyAdapters.BuildValidationScript(probeUri, allowSafeAdvance: false));
                    Trace?.Step("Validate", $"post-Next probe {attempt + 1}", probeJson);
                    using var probe = JsonDocument.Parse(probeJson);
                    nextErrors = ValidationErrors(probe.RootElement);
                    if (nextErrors.Count > 0) break;
                    var probeAction = probe.RootElement.TryGetProperty("action", out var probeActionElement)
                        ? probeActionElement.GetString() ?? "none"
                        : "none";
                    if (probeAction is "success" or "final" ||
                        !string.Equals(sourceBeforeNext, JobSiteBrowser.CoreWebView2?.Source,
                            StringComparison.OrdinalIgnoreCase)) break;
                }
                if (nextErrors.Count > 0)
                {
                    UnsettleFields(nextErrors.Select(error => error.Question));
                    aggregate = aggregate with
                    {
                        Unfilled = aggregate.Unfilled.Concat(nextErrors.Select(error => error.Question))
                            .Where(value => value.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    notes.Add("Next validation found: " + string.Join("; ", nextErrors.Take(8)
                        .Select(error => error.Question == error.Message
                            ? error.Message
                            : $"{error.Question}: {error.Message}")));
                    return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes),
                        nextErrors, false);
                }
                if (TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var nextUri))
                    vm.AdapterName = JobSiteApplyAdapters.Resolve(nextUri).Name;
                // A multi-step form renders new controls without necessarily changing the URL, so the
                // page-key check in FillFieldsAsync would not notice. These are different fields.
                ResetSettledFields("advanced to the next step");
                var nextFill = await FillFieldsAsync(vm);
                aggregate = new FillOutcome(
                    nextFill.Adapter,
                    aggregate.Filled + nextFill.Filled,
                    aggregate.Skipped + nextFill.Skipped,
                    aggregate.Touched.Concat(nextFill.Touched).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    nextFill.Unfilled);
                continue;
            }

            if (action == "final" && TryCoordinates(root, out var submitX, out var submitY))
            {
                var sourceBeforeSubmit = JobSiteBrowser.CoreWebView2?.Source ?? "";
                Trace?.Step("Validate", "clicking final Submit", sourceBeforeSubmit);
                await DispatchBrowserMouseClickAsync(submitX, submitY);
                notes.Add("Clicked final Submit with browser-level mouse input.");

                for (var attempt = 0; attempt < 14; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 700 : 250);
                    if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var probeUri)) continue;
                    string probeJson;
                    try
                    {
                        probeJson = await JobSiteBrowser.ExecuteScriptAsync(
                            JobSiteApplyAdapters.BuildValidationScript(probeUri, allowSafeAdvance: false));
                    }
                    catch when (attempt < 13) { continue; }
                    Trace?.Step("Validate", $"post-Submit probe {attempt + 1}", probeJson);
                    using var probe = JsonDocument.Parse(probeJson);
                    var probeRoot = probe.RootElement;
                    var submitErrors = ValidationErrors(probeRoot);
                    if (submitErrors.Count > 0)
                    {
                        aggregate = aggregate with
                        {
                            Unfilled = aggregate.Unfilled.Concat(submitErrors.Select(error => error.Question))
                                .Where(value => value.Length > 0)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                        };
                        notes.Add("Submit validation found: " + string.Join("; ", submitErrors.Take(8)
                            .Select(error => error.Question == error.Message
                                ? error.Message
                                : $"{error.Question}: {error.Message}")));
                        return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes),
                            submitErrors, false);
                    }

                    var probeAction = probeRoot.TryGetProperty("action", out var probeActionElement)
                        ? probeActionElement.GetString() ?? "none"
                        : "none";
                    if (probeAction == "success" || (attempt >= 4 && probeAction == "none"))
                    {
                        notes.Add("The job site accepted the application.");
                        return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes),
                            Array.Empty<ValidationError>(), true);
                    }
                }

                await InstallHumanSubmitObserverAsync();
                notes.Add("Submit was clicked, but the site showed neither confirmation nor a readable error; review the visible result.");
            }
            else
            {
                // Nothing was committed, so nothing rejected anything, so the correction round has
                // no errors to work from and never runs. That is a silent no-verification, and it
                // reads afterwards exactly like a form that passed — worth saying out loud.
                Trace?.Warn("Validate", "no Submit or Next located",
                    $"action={action}; the form was never committed, so no field errors could be read");
                notes.Add("No intermediate application action remains; review the visible form.");
            }
            return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes),
                Array.Empty<ValidationError>(), false);
        }
        notes.Add("Stopped after five application steps; review before continuing.");
        return new ApplicationValidationOutcome(aggregate, string.Join(" ", notes),
            Array.Empty<ValidationError>(), false);
    }

    private static bool TryCoordinates(JsonElement root, out double x, out double y)
    {
        x = y = 0;
        return root.TryGetProperty("x", out var xElement) && xElement.TryGetDouble(out x) &&
               root.TryGetProperty("y", out var yElement) && yElement.TryGetDouble(out y);
    }

    private async Task InstallHumanSubmitObserverAsync()
    {
        if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var uri)) return;
        var json = await JobSiteBrowser.ExecuteScriptAsync(
            JobSiteApplyAdapters.BuildHumanSubmitObserverScript(uri));
        Trace?.Step("Validate", "human Submit observer", json);
    }

    private static IReadOnlyList<ValidationError> ValidationErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return Array.Empty<ValidationError>();
        return errors.EnumerateArray().Select(error =>
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                var value = error.GetString()?.Trim() ?? "";
                return new ValidationError(value, value);
            }
            var question = error.TryGetProperty("question", out var questionElement)
                ? questionElement.GetString()?.Trim() ?? ""
                : "";
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()?.Trim() ?? ""
                : "";
            return new ValidationError(question.Length > 0 ? question : message, message.Length > 0 ? message : question);
        }).Where(error => error.Question.Length > 0 || error.Message.Length > 0).ToArray();
    }

    private async Task<FillOutcome> FillFieldsAsync(JobBrowserViewModel vm,
        IReadOnlyCollection<string>? forceLabels = null)
    {
        if (!TryGetHttpUri(JobSiteBrowser.CoreWebView2?.Source, out var uri))
            throw new InvalidOperationException("No application page is open.");
        var values = vm.BuildFillValues();
        Trace?.Step("Fill", "values assembled", $"{values.Count} key(s): " + string.Join(", ", values.Keys.Take(25)));

        var page = uri.GetLeftPart(UriPartial.Query);
        if (!string.Equals(page, _settledPage, StringComparison.OrdinalIgnoreCase))
        {
            ResetSettledFields("page is now " + page);
            _settledPage = page;
        }
        // Anything the caller forces is being corrected, so it is no longer settled whatever the
        // ledger says - drop it before the scripts read the ledger, not after.
        if (forceLabels is { Count: > 0 }) UnsettleFields(forceLabels);
        var settled = _settledFields.Values.ToArray();
        if (settled.Length > 0)
            Trace?.Step("Fill", $"skipping {settled.Length} settled field(s)", string.Join(" | ", settled.Take(20)));

        var json = await JobSiteBrowser.ExecuteScriptAsync(
            JobSiteFormAdapters.BuildFillScript(uri, values, forceLabels, settled));
        Trace?.Payload("Fill", "fill script returned", json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Text controls are deliberately driven one at a time. Ashby persists each blur through an
        // asynchronous form update; batching them allowed stale responses to erase random fields.
        var (textPlanned, textFilled, textLabels, textTouched) = await FillTextFieldsSequentiallyAsync();

        // React button/radio state must be committed through the browser input pipeline. Calling
        // element.click() can paint aria-pressed=true while Ashby's submission model stays empty.
        var (choicePlanned, choiceFilled, choiceLabels, choiceTouched) = await FillChoiceFieldsAsync();

        // Custom dropdowns are driven after the plain fields, because typing into one opens an
        // overlay that would sit on top of anything still to be filled.
        var (comboFilled, comboTouched) = await FillCustomDropdownsAsync(values, forceLabels, settled);

        await Task.Delay(800);
        var outstandingJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.OutstandingFieldsScript);
        var outstanding = JsonSerializer.Deserialize<string[]>(outstandingJson) ?? [];
        // Ashby's chosen-value element has a generated class, so an exact option click can persist
        // even when the generic visual probe cannot identify it. Do not report that confirmed click
        // as empty; the site itself remains the final authority at human review.
        outstanding = outstanding.Where(item => !comboTouched.Any(label =>
            NormalizeFieldLabel(item).StartsWith(NormalizeFieldLabel(label), StringComparison.Ordinal))).ToArray();

        var textKeys = textLabels.Select(NormalizeFieldLabel).ToHashSet(StringComparer.Ordinal);
        var choiceKeys = choiceLabels.Select(NormalizeFieldLabel).ToHashSet(StringComparer.Ordinal);
        var nonTextTouched = StringList(root, "touched")
            .Where(item => !textKeys.Contains(NormalizeFieldLabel(item)) &&
                           !choiceKeys.Contains(NormalizeFieldLabel(item)));

        // Only verified values enter the ledger. Every one of these lists is written after a read-back
        // confirmed the control holds what we typed, so a field that silently refused the value stays
        // out and gets another attempt on the next pass.
        MarkFieldsSettled(textTouched.Concat(choiceTouched).Concat(comboTouched));

        return new FillOutcome(
            root.GetProperty("adapter").GetString() ?? "Default (generic)",
            Math.Max(0, root.GetProperty("filled").GetInt32() - textPlanned - choicePlanned) +
            textFilled + choiceFilled + comboFilled,
            root.GetProperty("skipped").GetInt32(),
            nonTextTouched.Concat(textTouched).Concat(choiceTouched).Concat(comboTouched)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            outstanding);
    }

    private async Task<(int Planned, int Filled, List<string> Labels, List<string> Touched)>
        FillTextFieldsSequentiallyAsync()
    {
        var planJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.TextFieldPlanScript);
        Trace?.Payload("Text fill", "sequential plan", planJson);
        using var plan = JsonDocument.Parse(planJson);
        var labels = new List<string>();
        var touched = new List<string>();
        if (plan.RootElement.ValueKind != JsonValueKind.Array) return (0, 0, labels, touched);

        foreach (var entry in plan.RootElement.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var label = entry.GetProperty("label").GetString() ?? "";
            var value = entry.GetProperty("value").GetString() ?? "";
            labels.Add(label);
            var persisted = false;
            for (var attempt = 1; attempt <= 2 && !persisted; attempt++)
            {
                var targetJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildTextFieldTargetScript(index));
                Trace?.Step("Text fill", $"targeted \"{label}\"", $"attempt={attempt}: {targetJson}");
                using var target = JsonDocument.Parse(targetJson);
                if (!target.RootElement.TryGetProperty("ok", out var targetOk) || !targetOk.GetBoolean()) break;
                await DispatchBrowserMouseClickAsync(target.RootElement.GetProperty("x").GetDouble(),
                    target.RootElement.GetProperty("y").GetDouble());
                await DispatchBrowserTextEntryAsync(value);
                Trace?.Step("Text fill", $"typed \"{label}\"",
                    $"attempt={attempt}, browser input, length={value.Length}");

                await Task.Delay(attempt == 1 ? 600 : 900);
                var verifiedJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildTextFieldVerifyScript(index));
                Trace?.Step("Text fill", $"verified \"{label}\"", verifiedJson);
                using var verified = JsonDocument.Parse(verifiedJson);
                persisted = verified.RootElement.TryGetProperty("ok", out var verifiedOk) && verifiedOk.GetBoolean();
            }
            if (persisted) touched.Add(label);
            else Trace?.Warn("Text fill", $"value did not persist for \"{label}\"", "left for human review");
        }
        return (labels.Count, touched.Count, labels, touched);
    }

    private async Task DispatchBrowserTextEntryAsync(string value)
    {
        var core = JobSiteBrowser.CoreWebView2 ?? throw new InvalidOperationException("The job browser is unavailable.");
        async Task KeyAsync(string type, string key, string code, int virtualKey, int modifiers = 0) =>
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type, key, code, windowsVirtualKeyCode = virtualKey,
                nativeVirtualKeyCode = virtualKey, modifiers
            }));

        await KeyAsync("rawKeyDown", "Control", "ControlLeft", 17, 2);
        await KeyAsync("rawKeyDown", "a", "KeyA", 65, 2);
        await KeyAsync("keyUp", "a", "KeyA", 65, 2);
        await KeyAsync("keyUp", "Control", "ControlLeft", 17);
        await KeyAsync("rawKeyDown", "Backspace", "Backspace", 8);
        await KeyAsync("keyUp", "Backspace", "Backspace", 8);

        foreach (var character in value.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (character == '\n') await DispatchBrowserEnterAsync();
            else await core.CallDevToolsProtocolMethodAsync("Input.insertText",
                JsonSerializer.Serialize(new { text = character.ToString() }));
        }

        await KeyAsync("rawKeyDown", "Tab", "Tab", 9);
        await KeyAsync("keyUp", "Tab", "Tab", 9);
    }

    private async Task<(int Planned, int Filled, List<string> Labels, List<string> Touched)>
        FillChoiceFieldsAsync()
    {
        var planJson = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.ChoiceFieldPlanScript);
        Trace?.Payload("Choice fill", "browser-input plan", planJson);
        using var plan = JsonDocument.Parse(planJson);
        var labels = new List<string>();
        var touched = new List<string>();
        if (plan.RootElement.ValueKind != JsonValueKind.Array) return (0, 0, labels, touched);

        foreach (var entry in plan.RootElement.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var label = entry.GetProperty("label").GetString() ?? "";
            var option = entry.GetProperty("option").GetString() ?? "";
            var force = entry.TryGetProperty("force", out var forceElement) && forceElement.GetBoolean();
            labels.Add(label);
            var persisted = false;
            for (var attempt = 1; attempt <= 2 && !persisted; attempt++)
            {
                if (force && attempt == 1)
                {
                    var resetJson = await JobSiteBrowser.ExecuteScriptAsync(
                        JobSiteFormAdapters.BuildChoiceResetTargetScript(index));
                    Trace?.Step("Choice fill", $"resetting \"{label}\"", resetJson);
                    using var reset = JsonDocument.Parse(resetJson);
                    if (reset.RootElement.TryGetProperty("ok", out var resetOk) && resetOk.GetBoolean())
                    {
                        await DispatchBrowserMouseClickAsync(reset.RootElement.GetProperty("x").GetDouble(),
                            reset.RootElement.GetProperty("y").GetDouble());
                        await Task.Delay(400);
                    }
                }
                var targetJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildChoiceTargetScript(index));
                Trace?.Step("Choice fill", $"targeted \"{label}\"",
                    $"option=\"{option}\", attempt={attempt}: {targetJson}");
                using var target = JsonDocument.Parse(targetJson);
                if (!target.RootElement.TryGetProperty("ok", out var targetOk) || !targetOk.GetBoolean()) break;
                var x = target.RootElement.GetProperty("x").GetDouble();
                var y = target.RootElement.GetProperty("y").GetDouble();
                await DispatchBrowserMouseClickAsync(x, y);
                await Task.Delay(attempt == 1 ? 450 : 700);
                var verifiedJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildChoiceVerifyScript(index));
                Trace?.Step("Choice fill", $"verified \"{label}\"", verifiedJson);
                using var verified = JsonDocument.Parse(verifiedJson);
                persisted = verified.RootElement.TryGetProperty("ok", out var verifiedOk) && verifiedOk.GetBoolean();
            }
            if (persisted) touched.Add(label);
            else Trace?.Warn("Choice fill", $"selection did not persist for \"{label}\"", option);
        }
        return (labels.Count, touched.Count, labels, touched);
    }

    private async Task DispatchBrowserMouseClickAsync(double x, double y)
    {
        var core = JobSiteBrowser.CoreWebView2 ?? throw new InvalidOperationException("The job browser is unavailable.");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mouseMoved", x, y, button = "none", buttons = 0, pointerType = "mouse"
        }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1, pointerType = "mouse"
        }));
        await Task.Delay(45);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1, pointerType = "mouse"
        }));
    }

    /// <summary>
    /// Works each React combobox the way a person does: focus it, type the answer so its list
    /// filters, wait for that list to render, choose an exact option when possible, then verify the
    /// rendered chosen value. Enter is only a dropdown fallback. The waits are why this cannot live
    /// inside the fill script — ExecuteScriptAsync returns immediately on a promise.
    /// </summary>
    private async Task<(int Filled, List<string> Touched)> FillCustomDropdownsAsync(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? forceLabels, IReadOnlyCollection<string>? settledLabels = null)
    {
        var touched = new List<string>();
        var planJson = await JobSiteBrowser.ExecuteScriptAsync(
            JobSiteFormAdapters.BuildComboboxPlanScript(values, forceLabels, settledLabels));
        Trace?.Payload("Dropdown", "plan", planJson);
        using var plan = JsonDocument.Parse(planJson);
        if (plan.RootElement.ValueKind != JsonValueKind.Array) return (0, touched);

        foreach (var entry in plan.RootElement.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var value = entry.GetProperty("value").GetString() ?? "";
            var label = entry.GetProperty("label").GetString() ?? "";
            if (value.Length == 0) continue;

            var openedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildComboboxOpenScript(index));
            Trace?.Step("Dropdown", $"activated \"{label}\"", openedJson);
            await Task.Delay(250);
            var typedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildComboboxTypeScript(index, value));
            using (var firstTyped = JsonDocument.Parse(typedJson))
            {
                if (!firstTyped.RootElement.TryGetProperty("ok", out var firstOk) || !firstOk.GetBoolean())
                {
                    // Some widgets mount their searchable input on a second animation frame.
                    await Task.Delay(350);
                    typedJson = await JobSiteBrowser.ExecuteScriptAsync(
                        JobSiteFormAdapters.BuildComboboxTypeScript(index, value));
                }
            }
            Trace?.Step("Dropdown", $"typed into \"{label}\"", $"value=\"{value}\" -> {typedJson}");
            using var typed = JsonDocument.Parse(typedJson);
            if (!typed.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) continue;

            await Task.Delay(450);
            var committedJson = await JobSiteBrowser.ExecuteScriptAsync(
                JobSiteFormAdapters.BuildComboboxCommitScript(index, value));
            Trace?.Step("Dropdown", $"committed \"{label}\"", committedJson);
            using var committed = JsonDocument.Parse(committedJson);
            var commitMethod = committed.RootElement.TryGetProperty("method", out var method)
                ? method.GetString() ?? ""
                : "";
            var browserCommitted = false;
            if (committed.RootElement.TryGetProperty("ok", out var commitOk) && commitOk.GetBoolean())
            {
                if (commitMethod == "mouse-target" &&
                    committed.RootElement.TryGetProperty("x", out var xElement) &&
                    committed.RootElement.TryGetProperty("y", out var yElement))
                {
                    await DispatchBrowserMouseClickAsync(xElement.GetDouble(), yElement.GetDouble());
                    browserCommitted = true;
                }
                else if (commitMethod == "enter-target")
                {
                    await DispatchBrowserEnterAsync();
                }
            }
            // Poll rather than read once. Verification requires the menu to have closed and the
            // chosen value to have rendered, and react-select does both a frame or two after the
            // click — a single read at 450ms caught it mid-flight and reported ok:false every time.
            var confirmed = false;
            var verifiedJson = "";
            for (var attempt = 0; attempt < 6 && !confirmed; attempt++)
            {
                await Task.Delay(attempt == 0 ? 450 : 250);
                verifiedJson = await JobSiteBrowser.ExecuteScriptAsync(
                    JobSiteFormAdapters.BuildComboboxVerifyScript(index, value));
                using var probe = JsonDocument.Parse(verifiedJson);
                confirmed = probe.RootElement.TryGetProperty("ok", out var done) && done.GetBoolean();
            }
            Trace?.Step("Dropdown", $"verified \"{label}\"",
                $"{verifiedJson} (committed={browserCommitted})");
            // Having clicked is not the same as having chosen. Treating the click as success is why
            // four dropdowns were reported filled in the same run whose verification said they were
            // empty — and a field nobody looked at is worse than one flagged for review.
            if (confirmed) touched.Add(label.Length > 0 ? label : value);
            else
            {
                Trace?.Warn("Dropdown", $"not confirmed \"{label}\"",
                    $"wanted \"{value}\"; leaving it for review");
                await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.BuildComboboxCloseScript(index));
            }
        }
        return (touched.Count, touched);
    }

    private async Task DispatchBrowserEnterAsync()
    {
        var core = JobSiteBrowser.CoreWebView2 ?? throw new InvalidOperationException("The job browser is unavailable.");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "rawKeyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13,
            nativeVirtualKeyCode = 13
        }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13,
            nativeVirtualKeyCode = 13
        }));
    }

    private sealed record FillOutcome(
        string Adapter, int Filled, int Skipped,
        IReadOnlyList<string> Touched, IReadOnlyList<string> Unfilled);

    private sealed record ApplicationValidationOutcome(
        FillOutcome Fill,
        string Note,
        IReadOnlyList<ValidationError> Errors,
        bool Submitted);
    private sealed record ValidationError(string Question, string Message);

    private static IReadOnlyList<string> StringList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// Why the generated resume cannot be uploaded, or an empty string when it can.
    ///
    /// <para>
    /// The macro writes a .docm and exports a PDF beside it, and a run that failed partway can leave
    /// a file that exists, has a .pdf name, and is not a PDF. "File.Exists" was the whole check, so
    /// that file was uploaded and the application went out looking complete.
    /// </para>
    /// </summary>
    private static string RejectResumeFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "the generated resume file is missing";
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return $"the generated resume is not a PDF ({Path.GetExtension(path)})";
        if (info.Length < 8 * 1024) return $"the generated resume is only {info.Length} bytes";
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            return stream.ReadAtLeast(header, 5, throwOnEndOfStream: false) == 5 && header.StartsWith("%PDF"u8)
                ? ""
                : "the generated resume does not start with a PDF header";
        }
        catch (IOException ex) { return "the generated resume could not be read: " + ex.Message; }
    }

    private static string NormalizeFieldLabel(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Drops the ledger when the form underneath it changed.</summary>
    private void ResetSettledFields(string reason)
    {
        if (_settledFields.Count > 0)
            Trace?.Step("Fill", "settled ledger cleared", $"{_settledFields.Count} field(s): {reason}");
        _settledFields.Clear();
    }

    /// <summary>Records fields the host typed and verified, so later passes leave them alone.</summary>
    private void MarkFieldsSettled(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var key = NormalizeFieldLabel(label);
            if (key.Length > 0) _settledFields[key] = label;
        }
    }

    /// <summary>
    /// Releases fields the form complained about. An error is the only evidence that outranks our
    /// own verification: the value is in the input, and the site still refuses it.
    /// </summary>
    private void UnsettleFields(IEnumerable<string> labels)
    {
        var released = new List<string>();
        foreach (var label in labels)
        {
            var key = NormalizeFieldLabel(label);
            if (key.Length == 0) continue;
            // Site error text rarely repeats the label verbatim - "Phone" against "Phone number".
            foreach (var settled in _settledFields.Keys.Where(settled => settled == key ||
                         Math.Min(settled.Length, key.Length) >= 6 &&
                         (settled.Contains(key, StringComparison.Ordinal) ||
                          key.Contains(settled, StringComparison.Ordinal))).ToArray())
            {
                released.Add(_settledFields[settled]);
                _settledFields.Remove(settled);
            }
        }
        if (released.Count > 0)
            Trace?.Step("Fill", "released for retry", string.Join(" | ", released));
    }

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
        Trace?.Step("Upload", "resume input search",
            input == null ? "no suitable file input" : $"backendNodeId={input.Value.BackendNodeId}, score={input.Value.Score}");
        if (input == null)
        {
            if (reportStatus) vm.StatusMessage = "No resume input was found. Open the application form or upload manually.";
            return false;
        }
        var parameters = JsonSerializer.Serialize(new { files = new[] { vm.SelectedResumePath }, backendNodeId = input.Value.BackendNodeId });
        try
        {
            // Bounded on purpose. If the page rerendered between finding the node and setting it,
            // or the browser is showing a picker of its own, this call can simply never come back —
            // and an unbounded await here stops the application with nothing on screen to explain it.
            await JobSiteBrowser.CoreWebView2
                .CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", parameters)
                .WaitAsync(TimeSpan.FromSeconds(15));
            await NotifyFileInputAsync(input.Value.BackendNodeId).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Trace?.Warn("Upload", "attach timed out", "the browser did not answer; attach the resume by hand");
            if (reportStatus) vm.StatusMessage = "The resume upload did not complete. Attach it manually.";
            return false;
        }
        var fileName = Path.GetFileName(vm.SelectedResumePath);
        if (TryGetHttpUri(JobSiteBrowser.CoreWebView2.Source, out var uri)) vm.RecordUpload(uri.Host, fileName);
        Trace?.Ok("Upload", "resume attached", $"{fileName} from {vm.SelectedResumePath}");
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
