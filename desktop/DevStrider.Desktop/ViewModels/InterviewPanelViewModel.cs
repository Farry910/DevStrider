using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

public partial class InterviewPanelViewModel : ViewModelBase
{
    private readonly InterviewService _service;

    public ObservableCollection<Interview> Items { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-7);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(14);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); } }

    public InterviewPanelViewModel(InterviewService service)
    {
        _service = service;
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var fromUtc = From.ToUniversalTime();
            var toUtc = To.ToUniversalTime();
            var rows = await _service.ListAsync(fromUtc, toUtc);
            Items.Clear();
            foreach (var i in rows) Items.Add(i);
            StatusMessage = $"{rows.Count} interviews.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Parameter is <c>object?</c> to tolerate WPF passing <c>UnsetValue</c>; see BidBoardViewModel.</summary>
    [RelayCommand]
    public async Task SaveAsync(object? param)
    {
        if (param is not Interview iv) return;
        if (iv.Id == default) await _service.CreateAsync(iv);
        else await _service.UpdateAsync(iv);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task DeleteAsync(object? param)
    {
        if (param is not Interview iv) return;

        var label = $"{iv.Company} · {iv.Role} · {iv.InterviewType}".Trim(' ', '·');
        if (string.IsNullOrWhiteSpace(label)) label = "this interview";
        var when = iv.ScheduledDate?.ToString("MMM dd yyyy") ?? "(no date)";

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Delete interview?",
            $"{label}\nScheduled: {when}\n\nThis can't be undone.");
        if (!ok) return;

        await _service.DeleteAsync(iv.Id);
        await ReloadAsync();
        StatusMessage = $"Deleted: {label}";
    }

    /// <summary>Open a modal with the interview's attached JD text.</summary>
    public string GetJdFor(Interview iv) =>
        (iv?.AttachedJobDescription ?? "").Trim();

    /// <summary>
    /// Schedule a NEXT-step interview chained from this one. New interview captures the same
    /// company/role/resumeId/JD and points <c>ParentInterviewId</c> at the source.
    /// </summary>
    public async Task ScheduleNextStepAsync(
        Interview parent, DateTime? date, string time, string interviewType,
        string recruiter, string meetingLink)
    {
        if (parent == null) return;
        await _service.CreateAsync(new Interview
        {
            BidId = parent.BidId,
            ParentInterviewId = parent.Id,
            ScheduledDate = date,
            ScheduledTime = time,
            InterviewType = string.IsNullOrWhiteSpace(interviewType) ? InterviewTypes.Interview : interviewType,
            Recruiter = recruiter,
            MeetingLink = meetingLink,
            Company = parent.Company,
            Role = parent.Role,
            ResumeId = parent.ResumeId,
            AttachedJobDescription = parent.AttachedJobDescription,
            Status = InterviewStatuses.Scheduled,
            Origin = "NextStep"
        });
        await ReloadAsync();
        StatusMessage = $"Next-step {interviewType} scheduled for {parent.Company}.";
    }
}
