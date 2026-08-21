using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Shared state for the embedded job browser. WebView code supplies extracted visible text;
/// the user then copies it into the same ChatGPT session used by Resume Studio.
/// </summary>
public sealed partial class JobBrowserViewModel : ViewModelBase
{
    private string _address = "https://www.linkedin.com/jobs/";
    public string Address { get => _address; set => SetProperty(ref _address, value); }

    private string _jobDescription = "";
    public string JobDescription { get => _jobDescription; set => SetProperty(ref _jobDescription, value); }

    [RelayCommand]
    private void CopyExtractedJobDescription()
    {
        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            StatusMessage = "Extract a job description first.";
            return;
        }
        Clipboard.SetText("Job description:\n\n" + JobDescription.Trim());
        StatusMessage = "Job description copied. Paste it into the active ChatGPT resume conversation.";
    }
}
