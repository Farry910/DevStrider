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

    private BidTraceService? Trace => (DataContext as ResumeStudioViewModel)?.Trace;

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
        vm.AutoAnswerCorrectionRequested += SubmitAnswerCorrectionAsync;
        vm.AutoBidCancellationRequested += CancelAutomation;
        vm.NewChatRequested += StartFreshChat;
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel == null) return;
        _attachedViewModel.AutoResumeRequested -= SubmitAutomatedResumeAsync;
        _attachedViewModel.AutoAnswerCorrectionRequested -= SubmitAnswerCorrectionAsync;
        _attachedViewModel.AutoBidCancellationRequested -= CancelAutomation;
        _attachedViewModel.NewChatRequested -= StartFreshChat;
        _attachedViewModel.MarkChatGptBrowserUnavailable();
        _attachedViewModel = null;
    }

    private void CancelAutomation() => _automationCancellation?.Cancel();
    private void StartFreshChat() => ChatGptBrowser.CoreWebView2?.Navigate("https://chatgpt.com/");


    /// <summary>Whether a remembered conversation actually opened, and what was wrong if it did not.</summary>
    private readonly record struct ConversationCheck(bool Ok, string Detail);

    /// <summary>
    /// Confirms the conversation we navigated to is really open before anything is typed into it.
    ///
    /// <para>
    /// A continuation sends the job description alone, because the profile prompt is supposed to be
    /// sitting above it in that conversation. If the conversation did not open — deleted, a different
    /// account, ChatGPT throwing "unable to load" — then the prompt is not above it, and submitting
    /// anyway produces a resume written against nothing but a job description. That reads as a
    /// plausible resume and is not the one this profile asks for, which is the worst kind of wrong.
    /// </para>
    ///
    /// <para>
    /// Existing messages are the test that matters. The URL can still say /c/… and the composer can
    /// still be there on a conversation that failed to load its history, and it is the history that
    /// the continuation depends on.
    /// </para>
    /// </summary>
    private async Task<ConversationCheck> WaitForUsableConversationAsync(CancellationToken token)
    {
        const string probe = """
(() => {
  const text = (document.body && document.body.innerText || '').slice(0, 4000);
  const error = /unable to (load|access)[^.\n]{0,60}|conversation not found|this conversation (is |)unavailable|something went wrong|does not exist/i.exec(text);
  return {
    url: location.href,
    onConversation: /\/c\/[0-9a-zA-Z-]{8,}/.test(location.href),
    messages: document.querySelectorAll('[data-message-author-role]').length,
    composer: !!document.querySelector('#prompt-textarea,[contenteditable="true"],textarea'),
    error: error ? error[0] : '',
  };
})()
""";

        var last = "no reading";
        for (var attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(attempt == 0 ? 600 : 500, token);

            var raw = await ChatGptBrowser.ExecuteScriptAsync(probe);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var error = root.GetProperty("error").GetString() ?? "";
            var onConversation = root.GetProperty("onConversation").GetBoolean();
            var messages = root.GetProperty("messages").GetInt32();
            var composer = root.GetProperty("composer").GetBoolean();
            last = $"url={root.GetProperty("url").GetString()}, messages={messages}, " +
                   $"composer={composer}, error={(error.Length == 0 ? "none" : error)}";

            // An error banner will not clear itself, and being bounced off /c/ is final too.
            if (error.Length > 0) return new ConversationCheck(false, "ChatGPT said: " + error);
            if (!onConversation && attempt >= 2)
                return new ConversationCheck(false, "ChatGPT left the conversation URL: " + last);
            if (onConversation && composer && messages > 0) return new ConversationCheck(true, last);
        }
        return new ConversationCheck(false, "the conversation never finished loading: " + last);
    }

    private async void SubmitAutomatedResumeAsync(ChatGptResumeRequest request)
    {
        if (DataContext is not ResumeStudioViewModel vm || ChatGptBrowser.CoreWebView2 == null) return;
        _automationCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _automationCancellation = cancellation;

        try
        {
            var token = cancellation.Token;
            // Go to the conversation this request belongs to before typing into it. Continuing a
            // chat used to mean "submit wherever the pane happens to be pointing", so one click in
            // ChatGPT's sidebar sent the next job description to a chat that had never seen the
            // profile prompt — and the reply came back with none of the resume's structure.
            var prompt = request.Prompt;
            if (request.StartFreshChat)
                await NavigateAsync("https://chatgpt.com/", token);
            else if (!string.IsNullOrWhiteSpace(request.ConversationUrl))
            {
                await NavigateAsync(request.ConversationUrl, token);
                // Navigating somewhere is not the same as arriving. A remembered conversation can be
                // gone, or belong to an account this browser is no longer signed into.
                var usable = await WaitForUsableConversationAsync(token);
                Trace?.Step("ChatGPT", "remembered chat opened", $"ok={usable.Ok}; {usable.Detail}");
                if (!usable.Ok)
                {
                    // Start over properly rather than sending a bare job description into a chat that
                    // never received the profile prompt.
                    await NavigateAsync("https://chatgpt.com/", token);
                    prompt = request.FreshChatPrompt;
                    await vm.NoteResumeChatRestartedAsync(usable.Detail);
                }
            }
            else
            {
                // A continuation carries the job description alone; the prompt it depends on is at
                // the top of a conversation we cannot name. Submitting here would put it wherever
                // the pane was left - the caller guards against this, and this is the second lock,
                // because the failure is silent and produces a resume that reads fine.
                vm.ReportAutomatedResumeFailure(request.WorkItemId,
                    "The resume chat could not be reopened, so the job description had nowhere safe " +
                    "to go. Use Start fresh chat and run this link again.");
                return;
            }

            Trace?.Step("ChatGPT", "at conversation", ChatGptBrowser.CoreWebView2?.Source ?? "");
            var before = await GetAssistantSnapshotAsync();
            Trace?.Step("ChatGPT", "snapshot before prompt",
                $"messages={before.Count}, generating={before.Generating}");
            var submitted = await SubmitPromptAsync(prompt, token);
            Trace?.Step("ChatGPT", "prompt submitted", $"ok={submitted.Ok} {submitted.Error}");
            if (!submitted.Ok)
            {
                vm.ReportAutomatedResumeFailure(request.WorkItemId, submitted.Error);
                return;
            }

            vm.StatusMessage = "ChatGPT is generating the resume...";
            var resumeReply = await WaitForNewAssistantReplyAsync(before, token, FastFeed.HasSectionLabels);
            Trace?.Payload("ChatGPT", "resume reply", resumeReply, 900);
            if (!FastFeed.HasSectionLabels(resumeReply))
            {
                vm.ReportAutomatedResumeFailure(request.WorkItemId, DescribeUnusableReply(resumeReply));
                return;
            }

            var resumeConversationUrl = await WaitForConversationUrlAsync(token);
            Trace?.Step("ChatGPT", "conversation url",
                resumeConversationUrl.Length == 0 ? "(not resolved)" : resumeConversationUrl);
            if (string.IsNullOrWhiteSpace(resumeConversationUrl)) resumeConversationUrl = request.ConversationUrl;
            // Persist it before the questions step navigates away, so a crash mid-run still leaves
            // the resume chat findable next time.
            await vm.NoteConversationAsync(resumeConversationUrl);
            var answersJson = "{}";
            var answerConversationUrl = "";
            if (HasQuestions(request.QuestionsJson) && !string.IsNullOrWhiteSpace(resumeConversationUrl))
            {
                vm.StatusMessage = "Resume received. Asking ChatGPT for unanswered application fields...";
                // A queue retry after a restart continues the answer conversation persisted on the
                // work item. A brand-new application still starts a brand-new answer chat.
                await NavigateAsync(string.IsNullOrWhiteSpace(request.AnswerConversationUrl)
                    ? "https://chatgpt.com/"
                    : request.AnswerConversationUrl, token);
                var questionBefore = await GetAssistantSnapshotAsync();
                var questionPrompt = BuildQuestionPrompt(request, resumeReply);
                Trace?.Payload("ChatGPT", "question prompt", questionPrompt, 900);
                var questionSubmit = await SubmitPromptAsync(questionPrompt, token);
                Trace?.Step("ChatGPT", "question prompt submitted", $"ok={questionSubmit.Ok} {questionSubmit.Error}");
                if (!questionSubmit.Ok) throw new InvalidOperationException(questionSubmit.Error);
                // Hold out for something that actually parses. Without a shape to wait for, the first
                // text that stopped changing for two seconds was accepted — a pause mid-stream was
                // enough to capture half an object, which then silently became no answers at all.
                answersJson = await WaitForNewAssistantReplyAsync(questionBefore, token, LooksLikeAnswerJson);
                Trace?.Payload("ChatGPT", "answers reply", answersJson, 900);
                if (!LooksLikeAnswerJson(answersJson))
                {
                    vm.ReportUnusableAnswers(answersJson);
                    answersJson = "{}";
                }

                answerConversationUrl = await WaitForConversationUrlAsync(token);
                Trace?.Step("ChatGPT", "answer conversation url",
                    answerConversationUrl.Length == 0 ? "(not resolved)" : answerConversationUrl);
                if (!string.IsNullOrWhiteSpace(answerConversationUrl))
                    vm.NoteAnswerConversation(request.WorkItemId, answerConversationUrl);

                // The run used to navigate back to the resume chat here. It was housekeeping - the
                // answers were already in hand - but it was awaited, and a navigation that never
                // reports completion costs 45 seconds before it gives up. That wait sat between the
                // answers and the Word handoff, which is the worst place in the flow to spend time:
                // the pane shows the resume chat, nothing appears to be happening, and the run looks
                // stuck. Nothing needed it either. Every operation that follows navigates somewhere
                // explicit first - the next resume request to its conversation or a fresh chat, a
                // correction to the answer conversation - so where the pane happens to be left is
                // not an input to anything.
            }

            Trace?.Step("ChatGPT", "work complete", "handing to Word");
            vm.StatusMessage = "ChatGPT work complete. Saving the resume and running Word...";
            await vm.CompleteAutomatedResumeAsync(request, resumeReply, answersJson, answerConversationUrl);
        }
        catch (OperationCanceledException)
        {
            Trace?.Warn("ChatGPT", "cancelled", "automatic resume work stopped");
            vm.StatusMessage = "Automatic resume work stopped. Manual recovery is available.";
        }
        catch (Exception ex)
        {
            Trace?.Fail("ChatGPT", "threw", ex.ToString());
            vm.ReportAutomatedResumeFailure(request.WorkItemId, "ChatGPT automation failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_automationCancellation, cancellation)) _automationCancellation = null;
        }
    }

    private async void SubmitAnswerCorrectionAsync(ChatGptAnswerCorrectionRequest request)
    {
        if (DataContext is not ResumeStudioViewModel vm || ChatGptBrowser.CoreWebView2 == null) return;
        _automationCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _automationCancellation = cancellation;
        try
        {
            var token = cancellation.Token;
            await NavigateAsync(string.IsNullOrWhiteSpace(request.ConversationUrl)
                ? "https://chatgpt.com/"
                : request.ConversationUrl, token);
            Trace?.Step("ChatGPT", "answer correction conversation",
                ChatGptBrowser.CoreWebView2.Source ?? request.ConversationUrl);
            var before = await GetAssistantSnapshotAsync();
            var prompt = BuildAnswerCorrectionPrompt(request);
            Trace?.Payload("ChatGPT", "answer correction prompt", prompt, 1200);
            var submitted = await SubmitPromptAsync(prompt, token);
            if (!submitted.Ok) throw new InvalidOperationException(submitted.Error);
            vm.StatusMessage = "ChatGPT is choosing exact values for the dynamic fields...";
            var reply = await WaitForNewAssistantReplyAsync(before, token, LooksLikeAnswerJson);
            Trace?.Payload("ChatGPT", "answer correction reply", reply, 1200);
            var conversationUrl = await WaitForConversationUrlAsync(token);
            if (string.IsNullOrWhiteSpace(conversationUrl)) conversationUrl = request.ConversationUrl;
            vm.CompleteAnswerCorrection(request, reply, conversationUrl);
        }
        catch (OperationCanceledException)
        {
            vm.FailAnswerCorrection(request.WorkItemId, "Application-field correction was cancelled.");
        }
        catch (Exception ex)
        {
            Trace?.Fail("ChatGPT", "answer correction threw", ex.ToString());
            vm.FailAnswerCorrection(request.WorkItemId, "ChatGPT answer correction failed: " + ex.Message);
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


    /// <summary>
    /// Presses Enter in ChatGPT through the browser input pipeline.
    ///
    /// <para>
    /// Real key events rather than a synthetic KeyboardEvent, for the same reason the job browser
    /// uses them: ChatGPT's composer is a controlled editor, and a dispatched event that never went
    /// through the browser is not always treated as a keystroke.
    /// </para>
    /// </summary>
    private async Task DispatchChatGptEnterAsync()
    {
        var core = ChatGptBrowser.CoreWebView2 ?? throw new InvalidOperationException("ChatGPT browser is unavailable.");
        async Task KeyAsync(string type) =>
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type,
                key = "Enter",
                code = "Enter",
                windowsVirtualKeyCode = 13,
                nativeVirtualKeyCode = 13,
                text = "\r",
                unmodifiedText = "\r",
            }));

        await KeyAsync("rawKeyDown");
        await KeyAsync("char");
        await KeyAsync("keyUp");
    }

    /// <summary>
    /// Whether the composer emptied, which is how ChatGPT shows a message actually went.
    /// </summary>
    private async Task<bool> ComposerEmptiedAsync()
    {
        const string script = """
(() => {
  const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
  const inMessage = e => !!(e.closest && e.closest(
    '[data-message-author-role],[data-testid^="conversation-turn"],article'));
  const usable = e => !!e && visible(e) && !e.disabled && !e.readOnly && !inMessage(e);
  const composer = document.querySelector('#prompt-textarea');
  let input = usable(composer) ? composer : null;
  if (!input) {
    const candidates = Array.from(document.querySelectorAll(
      'form textarea,form [contenteditable="true"],textarea,[contenteditable="true"],[role="textbox"]'))
      .filter(usable);
    input = candidates[candidates.length - 1] || null;
  }
  if (!input) return true;
  return ((input.value !== undefined ? input.value : input.textContent) || '').trim().length === 0;
})()
""";
        var json = await ChatGptBrowser.ExecuteScriptAsync(script);
        return bool.TryParse(json, out var empty) && empty;
    }

    /// <summary>Clicks ChatGPT's own send control, for when Enter did not take.</summary>
    private async Task<bool> ClickSendButtonAsync()
    {
        const string script = """
(() => {
  const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
  const inMessage = e => !!(e.closest && e.closest(
    '[data-message-author-role],[data-testid^="conversation-turn"],article'));
  const send = Array.from(document.querySelectorAll('button')).find(button => {
    const label = ((button.getAttribute('aria-label') || '') + ' ' + (button.dataset.testid || '') +
                   ' ' + (button.title || '')).toLowerCase();
    return visible(button) && !button.disabled && !inMessage(button) &&
      (label.includes('send') || label.includes('submit') || button.getAttribute('data-testid') === 'send-button');
  });
  if (!send) return false;
  send.click();
  return true;
})()
""";
        var json = await ChatGptBrowser.ExecuteScriptAsync(script);
        return bool.TryParse(json, out var clicked) && clicked;
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
 // Anything inside the transcript is a message, not the composer. This mattered: taking the first
 // visible text input in document order meant that a reply left open for editing - an editable
 // wrapper sitting above the composer - captured the next prompt. The job description went into the
 // previous answer, nothing was ever sent, and the run stopped there.
 const inMessage = e => !!(e.closest && e.closest(
   '[data-message-author-role],[data-testid^="conversation-turn"],article'));
 const usable = e => !!e && visible(e) && !e.disabled && !e.readOnly && !inMessage(e);

 // Close an open editor first; while one is up ChatGPT will not accept a new message anyway.
 const editing = Array.from(document.querySelectorAll('textarea,[contenteditable="true"]'))
   .some(e => visible(e) && inMessage(e));
 if (editing) {
   const cancel = Array.from(document.querySelectorAll('button')).find(b => visible(b) &&
     /^(cancel|discard)$/i.test(((b.innerText || '') + ' ' + (b.getAttribute('aria-label') || '')).trim()));
   if (cancel) cancel.click();
   return { ok:false, waiting:true, error:'a reply was open for editing; closed it and retrying' };
 }

 const composer = document.querySelector('#prompt-textarea');
 let input = usable(composer) ? composer : null;
 if (!input) {
   // The composer is the last text input on the page; everything above it is transcript.
   const candidates = Array.from(document.querySelectorAll(
     'form textarea,form [contenteditable="true"],textarea,[contenteditable="true"],[role="textbox"]'))
     .filter(usable);
   input = candidates[candidates.length - 1] || null;
 }
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
 // Placing the text is as far as this goes. Sending used to depend entirely on finding an enabled
 // send button, and when ChatGPT did not present one the prompt just sat in the composer: text in,
 // nothing sent, and the run waiting three minutes for a reply to something never asked. The host
 // presses Enter instead, which is what a person does, and keeps the button as a fallback.
 const send = Array.from(document.querySelectorAll('button')).find(button => {
   const label = ((button.getAttribute('aria-label') || '') + ' ' + (button.dataset.testid || '') + ' ' + (button.title || '')).toLowerCase();
   // Same rule as the input: a Send inside a message belongs to that message s editor.
   return visible(button) && !button.disabled && !inMessage(button) &&
     (label.includes('send') || label.includes('submit') || button.getAttribute('data-testid') === 'send-button');
 });
 const rect = input.getBoundingClientRect();
 return { ok:true, waiting:false, error:'', hasSend:!!send,
   placed:((input.value !== undefined ? input.value : input.textContent) || '').trim().length,
   x:rect.left + rect.width / 2, y:rect.top + rect.height / 2 };
})()
""".Replace("__PROMPT__", payload);

            var json = await ChatGptBrowser.ExecuteScriptAsync(script);
            using var result = JsonDocument.Parse(json);
            var root = result.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var waiting = root.TryGetProperty("waiting", out var waitingValue) && waitingValue.GetBoolean();
                var error = root.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString() ?? "ChatGPT input is unavailable."
                    : "ChatGPT input is unavailable.";
                if (!waiting) return (false, error);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                continue;
            }

            var placed = root.TryGetProperty("placed", out var placedValue) ? placedValue.GetInt32() : 0;
            if (placed == 0)
            {
                Trace?.Warn("ChatGPT", "prompt did not land in the composer", "retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                continue;
            }

            // Send it the way a person does. The composer emptying is the proof it went; a send
            // button is only consulted when Enter did not take, and never assumed to exist.
            await DispatchChatGptEnterAsync();
            for (var settle = 0; settle < 6; settle++)
            {
                await Task.Delay(300, token);
                if (await ComposerEmptiedAsync())
                {
                    Trace?.Step("ChatGPT", "prompt sent", $"Enter, {placed} chars");
                    return (true, "");
                }
            }

            if (root.TryGetProperty("hasSend", out var hasSend) && hasSend.GetBoolean() &&
                await ClickSendButtonAsync())
            {
                for (var settle = 0; settle < 6; settle++)
                {
                    await Task.Delay(300, token);
                    if (await ComposerEmptiedAsync())
                    {
                        Trace?.Step("ChatGPT", "prompt sent", $"send button, {placed} chars");
                        return (true, "");
                    }
                }
            }

            Trace?.Warn("ChatGPT", "prompt stayed in the composer", $"attempt {attempt + 1}, {placed} chars");
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        return (false, "ChatGPT accepted the prompt but never sent it. Check the composer in Resume Studio.");
    }

    private async Task<AssistantSnapshot> GetAssistantSnapshotAsync()
    {
        const string script = """
(() => {
 const nodes = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'));
 const last = nodes.at(-1);
 // The reply is the rendered message body and nothing else. Taking innerText of the whole message
 // swept up what the free plan puts underneath it - the Copy/Rate/Retry row, and after that an
 // advertisement. That corrupts what goes to Word, and because an ad rotates on its own the text
 // never holds still, so the wait for a settled reply ran to its full timeout and the run stopped.
 const bodyOf = node => {
   if (!node) return '';
   const markdown = node.querySelector('.markdown, [data-message-content], .prose');
   if (markdown) return (markdown.innerText || '').trim();
   // Nothing named: keep children up to the first that holds a button. That is the action row, and
   // everything from there down is chrome. Structural, not word-matching - a resume is allowed to
   // contain the word "Retry".
   const parts = [];
   for (const child of Array.from(node.children)) {
     if (child.tagName === 'BUTTON' || child.querySelector('button')) break;
     parts.push(child.innerText || '');
   }
   const kept = parts.join('\n').trim();
   return kept || (node.innerText || '').trim();
 };
 const generating = Array.from(document.querySelectorAll('button')).some(button => {
   const label = ((button.getAttribute('aria-label') || '') + ' ' + (button.dataset.testid || '')).toLowerCase();
   return label.includes('stop') || label.includes('cancel');
 });
 return { count:nodes.length, text:bodyOf(last), generating };
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
    private async Task<string> WaitForNewAssistantReplyAsync(AssistantSnapshot before, CancellationToken token,
        Func<string, bool>? accept = null)
    {
        var priorText = "";
        var lastStable = "";
        var stableChecks = 0;
        var arrived = false;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            var snapshot = await GetAssistantSnapshotAsync();

            // Every fifteen seconds, say what is being waited for. A wait that ends in a three-
            // minute timeout used to leave nothing behind explaining which part never happened.
            if (attempt > 0 && attempt % 15 == 0)
                Trace?.Step("ChatGPT", $"still waiting, {attempt}s",
                    $"messages={snapshot.Count} (was {before.Count}), generating={snapshot.Generating}, " +
                    $"reply={snapshot.Text.Length} chars, changed={snapshot.Text != before.Text}");

            // A new reply is one whose text differs from the one that was last on screen. Counting
            // messages was the only test before, and it fails on a conversation being continued:
            // ChatGPT drops older turns out of the DOM as the transcript grows, so the count can
            // stay level or fall while a perfectly good reply is streaming in below it. That is why
            // continuing a chat timed out where a fresh one succeeded.
            var isNew = snapshot.Count > before.Count ||
                        (snapshot.Text.Length > 0 && snapshot.Text != before.Text);
            if (!arrived && isNew) arrived = true;

            if (!arrived || snapshot.Generating || string.IsNullOrWhiteSpace(snapshot.Text))
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
            var isNewText = snapshot.Text != lastStable;
            lastStable = snapshot.Text;
            var accepted = accept == null || accept(snapshot.Text);
            if (isNewText)
            {
                Trace?.Step("ChatGPT", "reply settled",
                    $"after {attempt}s, {snapshot.Text.Length} chars, accepted={accepted}");
                if (!accepted) Trace?.Payload("ChatGPT", "reply REJECTED by shape check", snapshot.Text, 1500);
            }
            if (accepted) return snapshot.Text;
        }

        // Three minutes gone. Which of the two happened matters: nothing ever arrived, or something
        // arrived and was refused every time for its shape.
        Trace?.Fail("ChatGPT", "gave up waiting for a reply", arrived
            ? $"a reply arrived ({lastStable.Length} chars) but never satisfied the shape check"
            : $"no new reply ever appeared; still showing the same {before.Text.Length} chars as before the prompt");
        return lastStable;
    }

    /// <summary>
    /// Whether a reply carries an answer object that will survive parsing. The whole answer set used
    /// to be thrown away by a silent <c>catch (JsonException) { return "{}"; }</c>, so a form filled
    /// from the profile alone and nothing said why.
    /// </summary>
    private static bool LooksLikeAnswerJson(string reply) => AnswerJson.TryExtract(reply, out _);

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
        "Questions arrive as JSON objects. When one carries \"options\", the answer must be exactly one " +
        "of them, copied character for character — the control only accepts its own wording, so anything " +
        "close but not identical is discarded. When it also carries \"multiple\": true, answer with a " +
        "comma-separated subset of those options. When it is marked \"type\": \"dropdown\" with no options, " +
        "the list could not be read, so answer with the short plain value most likely to appear in it. " +
        "A question without options must not come back empty either. Answer it in two to five " +
        "sentences from this person's real work — the methods they used, the scale, the metrics, the " +
        "outcome — and be concrete rather than general. " +
        "When the question names a domain they have not worked in, do not claim it and do not refuse: " +
        "answer with the closest genuine work they have done, say plainly which domain it was in, and " +
        "make the transferable part explicit. An honest adjacent answer is read; an empty required " +
        "field ends the application unread, which serves nobody. Declining to answer is not a neutral " +
        "choice here — it is the worst available outcome, and it is never the right one for an open " +
        "question. The narrow exception below still holds: never assert a credential, an employer, or " +
        "a project that the reference data does not contain. Describe real work accurately instead. " +
        "Answer in plain text only — no markdown, links, bullet characters or code fences, because " +
        "the text is typed straight into a form field. " +
        "Screening questions decide whether the application is read by anyone at all, so a required " +
        "question with options must never come back empty. When the reference data does not settle " +
        "one, choose the option that keeps the application eligible rather than returning nothing. " +
        "Consent to a background check or drug test, availability and start date, willingness to " +
        "relocate or travel, notice period, comfort with the stated work model, and acknowledgements " +
        "are the applicant's own choice, and the enabling option is the one to pick. " +
        "Read the direction of the question before choosing, because the eligible answer is not " +
        "always the word yes: \"do you consent to a background investigation\" is answered yes, while " +
        "\"will you require visa sponsorship\" is answered no — needing sponsorship is what " +
        "disqualifies. Pick by what the employer needs to be true, never by the wording. " +
        "One exception, and it is narrow: a checkable claim about this person that the reference data " +
        "does not state — citizenship or work authorisation, a degree, a licence, a certification, a " +
        "security clearance, or employment dates. Never assert one of those to pass a filter; return " +
        "an empty string so a human answers it. " +
        "Return ONLY valid JSON in this exact shape: {\"answers\":{\"exact question text\":\"answer\"}}, " +
        "keyed on the question text exactly as given and never on the options.\n\n" +
        "Reference data (profile and approved answers):\n" + request.KnownAnswersJson + "\n\n" +
        "Generated resume for this role:\n" + generatedResume + "\n\n" +
        "Questions:\n" + request.QuestionsJson + "\n\n" +
        "Job description:\n" + request.JobDescription;

    private static string BuildAnswerCorrectionPrompt(ChatGptAnswerCorrectionRequest request) =>
        "Correct only the application fields listed below. This is a second pass after the first " +
        "answers were typed into the live form. Each question contains the primary answer, the job site's " +
        "validation failure when available, and any options discovered from the live control. Treat the " +
        "validationErrors and failure values as direct feedback from the job site. For every question with options, " +
        "choose exactly one supplied option and copy it character for character. Do not invent an option. " +
        "For a question without options, return a corrected grounded value that addresses the stated failure. " +
        "A field the site is rejecting as required must not come back empty a second time: where the " +
        "reference data does not settle it, choose the option that keeps the application eligible — " +
        "consent, availability, willingness and acknowledgements are the applicant's own choice. Read " +
        "the direction first, since the eligible answer is not always yes: consent to a background " +
        "check is yes, while requiring visa sponsorship is no. Still never assert an unstated checkable " +
        "claim — citizenship or work authorisation, a degree, a licence, a clearance, employment dates — " +
        "leave those empty for a human. " +
        "Keep facts consistent with the reference and the earlier answers. Return ONLY valid JSON " +
        "in this exact shape: {\"answers\":{\"exact question text\":\"exact option or corrected answer\"}}.\n\n" +
        "Reference data:\n" + request.KnownAnswersJson + "\n\n" +
        "Earlier answers:\n" + request.CurrentAnswersJson + "\n\n" +
        "Fields that failed, site errors, and discovered options:\n" + request.QuestionsJson + "\n\n" +
        "Job description:\n" + request.JobDescription;

    private sealed record AssistantSnapshot(int Count, string Text, bool Generating);
}
