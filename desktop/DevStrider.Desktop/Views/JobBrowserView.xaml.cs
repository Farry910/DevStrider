using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;
using DevStrider.Desktop.Services;
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevStrider", "webview2", "job-sites");
            await JobSiteBrowser.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: path));
            JobSiteBrowser.CoreWebView2.SourceChanged += OnBrowserSourceChanged;
            OnNavigate(sender, e);
        }
        catch (Exception ex) when (DataContext is JobBrowserViewModel vm)
        {
            vm.StatusMessage = "Job browser couldn't start: " + ex.Message;
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || !Uri.TryCreate(vm.Address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            if (DataContext is JobBrowserViewModel invalid) invalid.StatusMessage = "Enter a valid https:// address.";
            return;
        }
        JobSiteBrowser.Source = uri;
        vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
    }

    private void OnBrowserSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm ||
            !Uri.TryCreate(JobSiteBrowser.CoreWebView2?.Source, UriKind.Absolute, out var uri)) return;
        vm.Address = uri.AbsoluteUri;
        vm.AdapterName = JobSiteFormAdapters.NameFor(uri);
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            var json = await JobSiteBrowser.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            vm.JobDescription = JsonSerializer.Deserialize<string>(json) ?? "";
            vm.StatusMessage = string.IsNullOrWhiteSpace(vm.JobDescription) ? "No visible page text was found." : "Visible page text extracted. Review it before copying.";
        }
        catch (Exception ex) { vm.StatusMessage = "Couldn't extract page text: " + ex.Message; }
    }

    private async void OnExtractQuestions(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null) return;
        try
        {
            var json = await JobSiteBrowser.ExecuteScriptAsync(JobSiteFormAdapters.QuestionsScript);
            vm.FormQuestionsJson = json;
            vm.StatusMessage = "Form questions extracted. Copy them to ChatGPT for reviewed answers.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Couldn't extract form questions: " + ex.Message;
            vm.RecordFailure("Question extraction failed", ex.Message);
        }
    }

    private async void OnFill(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null || JobSiteBrowser.Source == null) return;
        try
        {
            var script = JobSiteFormAdapters.BuildFillScript(JobSiteBrowser.Source, vm.BuildFillValues());
            var json = await JobSiteBrowser.ExecuteScriptAsync(script);
            using var result = JsonDocument.Parse(json);
            var root = result.RootElement;
            var adapter = root.GetProperty("adapter").GetString() ?? "Default (generic)";
            var filled = root.GetProperty("filled").GetInt32();
            var skipped = root.GetProperty("skipped").GetInt32();
            vm.StatusMessage = $"{adapter}: filled {filled} field(s); skipped {skipped}. Review the form before submitting.";
            vm.RecordFill(JobSiteBrowser.Source.Host, adapter, filled, skipped);
        }
        catch (JsonException ex)
        {
            vm.StatusMessage = "Answers must be valid JSON: " + ex.Message;
            vm.RecordWarning("Field fill skipped", "Invalid answer JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Couldn't fill fields: " + ex.Message;
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
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            vm.SelectedResumePath = Path.GetFullPath(dialog.FileName);
            vm.StatusMessage = "Resume selected. Review the filename, then choose Upload selected resume.";
        }
    }

    private async void OnUploadResume(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || JobSiteBrowser.CoreWebView2 == null || JobSiteBrowser.Source == null) return;
        if (string.IsNullOrWhiteSpace(vm.SelectedResumePath) || !File.Exists(vm.SelectedResumePath))
        {
            vm.StatusMessage = "Choose an existing resume file first.";
            return;
        }
        var extension = Path.GetExtension(vm.SelectedResumePath).ToLowerInvariant();
        if (extension is not (".pdf" or ".doc" or ".docx"))
        {
            vm.StatusMessage = "Choose a PDF, DOC, or DOCX resume file.";
            return;
        }

        try
        {
            var input = await FindResumeFileInputAsync();
            if (input == null)
            {
                vm.StatusMessage = "No resume file input was found on this page. Open the application form or upload it manually.";
                vm.RecordWarning("Resume upload skipped", $"No likely resume input on {JobSiteBrowser.Source.Host}");
                return;
            }

            var parameters = JsonSerializer.Serialize(new
            {
                files = new[] { vm.SelectedResumePath },
                backendNodeId = input.Value.BackendNodeId,
            });
            await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", parameters);
            await NotifyFileInputAsync(input.Value.BackendNodeId);

            var fileName = Path.GetFileName(vm.SelectedResumePath);
            vm.StatusMessage = $"Uploaded {fileName} to the detected resume input. Confirm the page shows the correct file before submitting.";
            vm.RecordUpload(JobSiteBrowser.Source.Host, fileName);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "Couldn't upload the resume: " + ex.Message;
            vm.RecordFailure("Resume upload failed", ex.Message);
        }
    }

    private async Task<(int BackendNodeId, int Score)?> FindResumeFileInputAsync()
    {
        await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
        var json = await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "DOM.getFlattenedDocument", "{\"depth\":-1,\"pierce\":true}");
        using var document = JsonDocument.Parse(json);
        (int BackendNodeId, int Score)? best = null;
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("nodeName", out var nodeName) ||
                !string.Equals(nodeName.GetString(), "INPUT", StringComparison.OrdinalIgnoreCase) ||
                !node.TryGetProperty("attributes", out var attributes)) continue;

            var parts = attributes.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            var typeIsFile = false;
            for (var i = 0; i + 1 < parts.Length; i += 2)
                if (parts[i].Equals("type", StringComparison.OrdinalIgnoreCase) &&
                    parts[i + 1].Equals("file", StringComparison.OrdinalIgnoreCase)) typeIsFile = true;
            if (!typeIsFile) continue;

            var description = string.Join(' ', parts).ToLowerInvariant();
            var score = 0;
            if (description.Contains("resume")) score += 100;
            if (description.Contains("curriculum") || description.Contains("cv")) score += 80;
            if (description.Contains("upload")) score += 10;
            if (description.Contains("cover")) score -= 100;
            if (description.Contains("portfolio") || description.Contains("photo") || description.Contains("avatar")) score -= 100;
            if (!node.TryGetProperty("backendNodeId", out var backendNodeId)) continue;
            var candidate = (BackendNodeId: backendNodeId.GetInt32(), Score: score);
            if (best == null || candidate.Score > best.Value.Score) best = candidate;
        }
        return best is { Score: >= 0 } ? best : null;
    }

    private async Task NotifyFileInputAsync(int backendNodeId)
    {
        var resolvedJson = await JobSiteBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "DOM.resolveNode", JsonSerializer.Serialize(new { backendNodeId }));
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
}
