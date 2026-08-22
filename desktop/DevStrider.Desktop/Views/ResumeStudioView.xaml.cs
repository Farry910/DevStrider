using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DevStrider.Desktop.Views;

public partial class ResumeStudioView : UserControl
{
    private bool _initialized;
    private bool _initializing;
    private ResumeStudioViewModel? _attachedViewModel;
    private CancellationTokenSource? _automationCancellation;

    public ResumeStudioView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // The workspace is built once and hidden, so it is collapsed when Loaded first fires and a
        // collapsed WebView2 has no window handle to initialize against. Retry when the tab is
        // actually shown rather than leaving a blank pane behind.
        IsVisibleChanged += async (_, _) => { if (IsVisible) await EnsureBrowserAsync(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await EnsureBrowserAsync();

    private async Task EnsureBrowserAsync()
    {
        AttachViewModel();
        if (_initialized)
        {
            _attachedViewModel?.MarkChatGptBrowserReady();
            return;
        }
        if (_initializing) return;
        _initializing = true;

        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevStrider", "webview2", "chatgpt");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await ChatGptBrowser.EnsureCoreWebView2Async(environment);
            _initialized = true;
            // A new CoreWebView2 reports about:blank rather than an empty source, so an emptiness
            // check skips the only navigation that matters and the pane stays blank for good.
            // Anything that is not already a real page means ChatGPT still has to be opened.
            var current = ChatGptBrowser.CoreWebView2.Source ?? "";
            if (!current.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                ChatGptBrowser.CoreWebView2.Navigate("https://chatgpt.com/");
            if (IsLoaded)
            {
                AttachViewModel();
                _attachedViewModel?.MarkChatGptBrowserReady();
            }
        }
        catch (Exception ex)
        {
            _initialized = false;
            if (_attachedViewModel != null)
                _attachedViewModel.StatusMessage = "ChatGPT browser couldn't start: " + ex.Message;
        }
        finally { _initializing = false; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void AttachViewModel()
    {
        if (ReferenceEquals(_attachedViewModel, DataContext)) return;
        DetachViewModel();
        if (DataContext is not ResumeStudioViewModel vm) return;
        _attachedViewModel = vm;
        vm.AutoResumeRequested += SubmitAutomatedResumeAsync;
        vm.AutoBidCancellationRequested += CancelAutomation;
        vm.NewChatRequested += StartFreshChat;
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel == null) return;
        _attachedViewModel.AutoResumeRequested -= SubmitAutomatedResumeAsync;
        _attachedViewModel.AutoBidCancellationRequested -= CancelAutomation;
        _attachedViewModel.NewChatRequested -= StartFreshChat;
        _attachedViewModel.MarkChatGptBrowserUnavailable();
        _attachedViewModel = null;
    }

    private void CancelAutomation() => _automationCancellation?.Cancel();
    private void StartFreshChat() => ChatGptBrowser.CoreWebView2?.Navigate("https://chatgpt.com/");

    private async void SubmitAutomatedResumeAsync(ChatGptResumeRequest request)
    {
        if (DataContext is not ResumeStudioViewModel vm || ChatGptBrowser.CoreWebView2 == null) return;
        _automationCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _automationCancellation = cancellation;

        try
        {
            var token = cancellation.Token;
            if (request.StartFreshChat)
                await NavigateAsync("https://chatgpt.com/", token);

            var before = await GetAssistantSnapshotAsync();
            var submitted = await SubmitPromptAsync(request.Prompt, token);
            if (!submitted.Ok)
            {
                vm.ReportAutomatedResumeFailure(request.WorkItemId, submitted.Error);
                return;
            }

            vm.StatusMessage = "ChatGPT is generating the resume...";
            var resumeReply = await WaitForNewAssistantReplyAsync(before.Count, token, FastFeed.HasSectionLabels);
            if (!FastFeed.HasSectionLabels(resumeReply))
            {
                vm.ReportAutomatedResumeFailure(request.WorkItemId, DescribeUnusableReply(resumeReply));
                return;
            }

            var resumeConversationUrl = await WaitForConversationUrlAsync(token);
            var answersJson = "{}";
            if (HasQuestions(request.QuestionsJson) && !string.IsNullOrWhiteSpace(resumeConversationUrl))
            {
                vm.StatusMessage = "Resume received. Asking ChatGPT for unanswered application fields...";
                await NavigateAsync("https://chatgpt.com/", token);
                var questionBefore = await GetAssistantSnapshotAsync();
                var questionSubmit = await SubmitPromptAsync(BuildQuestionPrompt(request, resumeReply), token);
                if (!questionSubmit.Ok) throw new InvalidOperationException(questionSubmit.Error);
                answersJson = await WaitForNewAssistantReplyAsync(questionBefore.Count, token);
                if (string.IsNullOrWhiteSpace(answersJson)) answersJson = "{}";

                if (!string.IsNullOrWhiteSpace(resumeConversationUrl))
                    await NavigateAsync(resumeConversationUrl, token);
            }

            vm.StatusMessage = "ChatGPT work complete. Saving the resume and running Word...";
            await vm.CompleteAutomatedResumeAsync(request, resumeReply, answersJson);
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "Automatic resume work stopped. Manual recovery is available.";
        }
        catch (Exception ex)
        {
            vm.ReportAutomatedResumeFailure(request.WorkItemId, "ChatGPT automation failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_automationCancellation, cancellation)) _automationCancellation = null;
        }
    }

    private async Task NavigateAsync(string url, CancellationToken token)
    {
        var core = ChatGptBrowser.CoreWebView2 ?? throw new InvalidOperationException("ChatGPT browser is unavailable.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= Handler;
            if (args.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException($"ChatGPT navigation failed: {args.WebErrorStatus}"));
        }
        core.NavigationCompleted += Handler;
        core.Navigate(url);
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token); }
        finally { core.NavigationCompleted -= Handler; }
    }

    private async Task<(bool Ok, string Error)> SubmitPromptAsync(string prompt, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(prompt);
        for (var attempt = 0; attempt < 45; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var script = """
(() => {
 const prompt = __PROMPT__;
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const input = Array.from(document.querySelectorAll('textarea,[contenteditable="true"],[role="textbox"]'))
   .find(e => visible(e) && !e.disabled && !e.readOnly);
 if (!input) return { ok:false, waiting:true, error:'ChatGPT input was not found. Sign in and dismiss any dialog.' };
 input.focus();
 if (input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement) {
   const proto = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
   const setter = Object.getOwnPropertyDescriptor(proto,'value')?.set;
   setter ? setter.call(input, prompt) : input.value = prompt;
 } else {
   input.textContent = prompt;
 }
 input.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:prompt}));
 input.dispatchEvent(new Event('change',{bubbles:true}));
 const send = Array.from(document.querySelectorAll('button')).find(button => {
   const label = ((button.getAttribute('aria-label') || '') + ' ' + (button.dataset.testid || '') + ' ' + (button.title || '')).toLowerCase();
   return visible(button) && !button.disabled &&
     (label.includes('send') || label.includes('submit') || button.getAttribute('data-testid') === 'send-button');
 });
 if (!send) return { ok:false, waiting:true, error:'ChatGPT send button was not found.' };
 send.click();
 return { ok:true, waiting:false, error:'' };
})()
""".Replace("__PROMPT__", payload);

            var json = await ChatGptBrowser.ExecuteScriptAsync(script);
            using var result = JsonDocument.Parse(json);
            var root = result.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return (true, "");
            var waiting = root.TryGetProperty("waiting", out var waitingValue) && waitingValue.GetBoolean();
            var error = root.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString() ?? "ChatGPT input is unavailable."
                : "ChatGPT input is unavailable.";
            if (!waiting) return (false, error);
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        return (false, "ChatGPT did not become ready. Sign in, dismiss any dialog, and retry.");
    }

    private async Task<AssistantSnapshot> GetAssistantSnapshotAsync()
    {
        const string script = """
(() => {
 const nodes = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'));
 const last = nodes.at(-1);
 const generating = Array.from(document.querySelectorAll('button')).some(button => {
   const label = ((button.getAttribute('aria-label') || '') + ' ' + (button.dataset.testid || '')).toLowerCase();
   return label.includes('stop') || label.includes('cancel');
 });
 return { count:nodes.length, text:last?.innerText?.trim() || '', generating };
})()
""";
        var json = await ChatGptBrowser.ExecuteScriptAsync(script);
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        return new AssistantSnapshot(
            root.TryGetProperty("count", out var count) ? count.GetInt32() : 0,
            root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            root.TryGetProperty("generating", out var generating) && generating.GetBoolean());
    }

    /// <summary>
    /// Waits for the reply to stop changing. <paramref name="accept"/> lets the caller insist on a
    /// shape: a message can sit perfectly stable and still be a refusal or a rate-limit notice, and
    /// settling for one of those is what sends junk to Word. The last stable text is returned on
    /// timeout regardless, so the caller can report what ChatGPT actually said.
    /// </summary>
    private async Task<string> WaitForNewAssistantReplyAsync(int previousCount, CancellationToken token,
        Func<string, bool>? accept = null)
    {
        var priorText = "";
        var lastStable = "";
        var stableChecks = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            var snapshot = await GetAssistantSnapshotAsync();
            if (snapshot.Count <= previousCount || snapshot.Generating || string.IsNullOrWhiteSpace(snapshot.Text))
            {
                stableChecks = 0;
                continue;
            }
            if (snapshot.Text == priorText) stableChecks++;
            else
            {
                priorText = snapshot.Text;
                stableChecks = 0;
            }
            if (stableChecks < 2) continue;
            lastStable = snapshot.Text;
            if (accept == null || accept(snapshot.Text)) return snapshot.Text;
        }
        return lastStable;
    }

    /// <summary>Reports what ChatGPT sent, so its answer is never filed as a Word fault.</summary>
    private static string DescribeUnusableReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return "ChatGPT did not reply within 3 minutes. Open Resume Studio to see the conversation, then use Manual recovery.";
        var snippet = reply.Length <= 200 ? reply : reply[..200] + "...";
        return "ChatGPT replied without the [Section]: labels the Word macro needs, so nothing was sent to Word. It said: "
               + snippet.Replace('\r', ' ').Replace('\n', ' ');
    }

    private async Task<string> WaitForConversationUrlAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var source = ChatGptBrowser.CoreWebView2?.Source ?? "";
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
                return uri.AbsoluteUri;
            await Task.Delay(500, token);
        }
        return "";
    }

    private static bool HasQuestions(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException) { return false; }
    }

    private static string BuildQuestionPrompt(ChatGptResumeRequest request, string generatedResume) =>
        "Answer the following job-application questions from the reference data below and nothing else. " +
        "The reference data is everything this person has told the app about themselves: their profile, " +
        "every answer they have already approved, and the resume just generated for this role. " +
        "Derive answers from it — years of experience, for instance, follow from the dated roles in the resume. " +
        "Never invent a fact that is not supported there; return an empty string for anything you cannot " +
        "ground in the reference data, and be consistent with previously approved answers. " +
        "Never answer a question asking for a government ID, social-security or national-insurance number, " +
        "passport, driver's licence, or bank or card details: return an empty string for those. " +
        "For a multiple-choice question return the chosen options as a comma-separated list. " +
        "Return ONLY valid JSON in this exact shape: {\"answers\":{\"exact question\":\"answer\"}}.\n\n" +
        "Reference data (profile and approved answers):\n" + request.KnownAnswersJson + "\n\n" +
        "Generated resume for this role:\n" + generatedResume + "\n\n" +
        "Questions:\n" + request.QuestionsJson + "\n\n" +
        "Job description:\n" + request.JobDescription;

    private sealed record AssistantSnapshot(int Count, string Text, bool Generating);
}
