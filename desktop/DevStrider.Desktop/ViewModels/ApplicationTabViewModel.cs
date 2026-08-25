using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// One open application: a filled form on screen, with its own browser behind it.
///
/// <para>
/// There used to be exactly one, so a finished application held the whole queue still until somebody
/// looked at it. Reviewing takes a minute or two of close attention, and the run could not start the
/// next resume until it was over — the machine sat idle waiting for a person, and the person was
/// interrupted once per link rather than once per batch.
/// </para>
///
/// <para>
/// Now a finished application is parked in its own tab and the run carries straight on into a new
/// one. Generation stays strictly one at a time, because it is driven through the ChatGPT UI and
/// there is one of those; what overlaps is the reviewing, which is the part that needed a person.
/// </para>
/// </summary>
public sealed class ApplicationTabViewModel(Guid workItemId, string url, string title) : ObservableObject
{
    /// <summary>The queue item this tab is showing. The tab closes when the item is finished.</summary>
    public Guid WorkItemId { get; } = workItemId;

    public string Url { get; } = url;

    private string _title = title;
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _status = "Working";
    /// <summary>Where this application got to, shown under the tab title.</summary>
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _summary = "";
    /// <summary>Filled and skipped counts, plus anything the run wants the reviewer to know.</summary>
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private bool _isAutomation = true;

    /// <summary>
    /// True while the run is driving this tab. Exactly one tab is the automation tab at a time, and
    /// it is the only one any script runs against.
    /// </summary>
    public bool IsAutomation
    {
        get => _isAutomation;
        set
        {
            if (!SetProperty(ref _isAutomation, value)) return;
            OnPropertyChanged(nameof(IsAwaitingReview));
        }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>A parked tab: filled, waiting for a person, not being driven by anything.</summary>
    public bool IsAwaitingReview => !IsAutomation;
}
