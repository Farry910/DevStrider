using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;
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
            OnNavigate(sender, e);
        }
        catch (Exception ex) when (DataContext is JobBrowserViewModel vm)
        {
            vm.StatusMessage = "Job browser couldn't start: " + ex.Message;
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JobBrowserViewModel vm || !Uri.TryCreate(vm.Address, UriKind.Absolute, out var uri))
        {
            if (DataContext is JobBrowserViewModel invalid) invalid.StatusMessage = "Enter a valid https:// address.";
            return;
        }
        JobSiteBrowser.Source = uri;
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
}
