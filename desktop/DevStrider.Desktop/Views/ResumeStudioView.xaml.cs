using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ChatGPT browser couldn't start: {ex.Message}", "DevStrider");
        }
    }

    private void OnOpenChatGpt(object sender, RoutedEventArgs e) =>
        ChatGptBrowser.Source = new Uri("https://chatgpt.com/");
}
