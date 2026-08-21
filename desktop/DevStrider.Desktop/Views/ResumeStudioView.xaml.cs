using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DevStrider.Desktop.Views;

public partial class ResumeStudioView : UserControl
{
    private bool _initialized;

    public ResumeStudioView()
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
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevStrider", "webview2", "chatgpt");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await ChatGptBrowser.EnsureCoreWebView2Async(environment);
            ChatGptBrowser.Source = new Uri("https://chatgpt.com/");
            if (DataContext is ResumeStudioViewModel vm)
            {
                vm.ChatGptFocusRequested += FocusChatGpt;
                vm.AutoBidRequested += SubmitAutomatedBidAsync;
                vm.MarkChatGptBrowserReady();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ChatGPT browser couldn't start: {ex.Message}", "DevStrider");
        }
    }

    private void OnOpenChatGpt(object sender, RoutedEventArgs e) =>
        ChatGptBrowser.Source = new Uri("https://chatgpt.com/");

    private void FocusChatGpt() =>
        Dispatcher.BeginInvoke(new Action(() => ChatGptBrowser.Focus()));

    private async void SubmitAutomatedBidAsync(ChatGptBidRequest request)
    {
        if (DataContext is not ResumeStudioViewModel vm || ChatGptBrowser.CoreWebView2 == null) return;
        try
        {
            var before = await GetAssistantSnapshotAsync();
            var submitted = await SubmitPromptAsync(request.Prompt);
            if (!submitted.Ok)
            {
                vm.ReportAutomatedBidFailure(submitted.Error);
                return;
            }

            vm.StatusMessage = "ChatGPT is generating the resume…";
            var reply = await WaitForNewAssistantReplyAsync(before.Count);
            if (string.IsNullOrWhiteSpace(reply))
            {
                vm.ReportAutomatedBidFailure("ChatGPT did not return a completed reply before the 3-minute timeout. Check the embedded chat, then use Finish from clipboard.");
                return;
            }

            vm.StatusMessage = "ChatGPT reply received. Saving the bid and generating the Word resume…";
            await vm.CompleteAutomatedBidAsync(reply);
        }
        catch (Exception ex)
        {
            vm.ReportAutomatedBidFailure("ChatGPT automation failed: " + ex.Message);
        }
    }

    private async Task<(bool Ok, string Error)> SubmitPromptAsync(string prompt)
    {
        var payload = JsonSerializer.Serialize(prompt);
        for (var attempt = 0; attempt < 45; attempt++)
        {
            var script = """
(() => {
 const prompt = __PROMPT__;
 const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
 const input = Array.from(document.querySelectorAll('textarea,[contenteditable="true"],[role="textbox"]'))
   .find(e => visible(e) && !e.disabled && !e.readOnly);
 if (!input) return { ok:false, waiting:true, error:'ChatGPT input was not found. Sign in and open a conversation.' };
 input.focus();
 if (input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement) {
   const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value')?.set ||
                  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
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
            if (root.GetProperty("ok").GetBoolean()) return (true, "");
            var waiting = root.TryGetProperty("waiting", out var waitingValue) && waitingValue.GetBoolean();
            var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() ?? "ChatGPT input is unavailable." : "ChatGPT input is unavailable.";
            if (!waiting) return (false, error);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return (false, "ChatGPT did not become ready. Sign in, dismiss any dialog, and try Start bid again.");
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

    private async Task<string> WaitForNewAssistantReplyAsync(int previousCount)
    {
        var priorText = "";
        var stableChecks = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
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
            if (stableChecks >= 2) return snapshot.Text;
        }
        return "";
    }

    private sealed record AssistantSnapshot(int Count, string Text, bool Generating);
}
