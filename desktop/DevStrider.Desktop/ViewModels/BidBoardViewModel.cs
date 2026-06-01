using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Bson;

namespace DevStrider.Desktop.ViewModels;

public partial class BidBoardViewModel : ViewModelBase
{
    private readonly BidBoardService _service;
    private readonly ProfileService _profiles;
    private readonly InterviewService? _interviews;

    public ObservableCollection<BoardRow> Rows { get; } = new();

    /// <summary>
    /// Day-mode picker. Setting it snaps From/To to that single day. The "Custom range"
    /// toggle below lets the user widen the window past a single day.
    /// </summary>
    private DateTime _selectedDay = DateTime.Today;
    public DateTime SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value))
            {
                if (!UseCustomRange)
                {
                    _from = value;
                    _to = value;
                    OnPropertyChanged(nameof(From));
                    OnPropertyChanged(nameof(To));
                }
                _ = ReloadAsync();
            }
        }
    }

    private bool _useCustomRange;
    /// <summary>When true the view uses <see cref="From"/>/<see cref="To"/>; otherwise just <see cref="SelectedDay"/>.</summary>
    public bool UseCustomRange
    {
        get => _useCustomRange;
        set { if (SetProperty(ref _useCustomRange, value)) _ = ReloadAsync(); }
    }

    private DateTime _from = DateTime.Today;
    public DateTime From
    {
        get => _from;
        set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); }
    }

    private DateTime _to = DateTime.Today;
    public DateTime To
    {
        get => _to;
        set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); }
    }

    private string _newLinkUrl = "";
    public string NewLinkUrl { get => _newLinkUrl; set => SetProperty(ref _newLinkUrl, value); }

    private string _newLinkSharedJd = "";
    public string NewLinkSharedJd { get => _newLinkSharedJd; set => SetProperty(ref _newLinkSharedJd, value); }

    public BidBoardViewModel(BidBoardService service, ProfileService profiles, InterviewService interviews)
    {
        _service = service;
        _profiles = profiles;
        _interviews = interviews;
    }

    /// <summary>
    /// Create an interview off the given bid, carrying the bid's <c>ResumeId</c> and JD into
    /// the interview row so the user has both ready at interview time. Called from the
    /// "Schedule interview" dialog on a bid row.
    /// </summary>
    public async Task ScheduleInterviewFromBidAsync(
        BoardRow row, DateTime? scheduledDate, string scheduledTime,
        string interviewType, string recruiter, string meetingLink)
    {
        if (_interviews == null || row?.Bid == null) return;
        var jd = (row.Bid.JobDescription ?? "").Trim();
        if (jd.Length == 0) jd = (row.Link?.SharedJobDescription ?? "").Trim();

        await _interviews.CreateAsync(new Models.Interview
        {
            BidId = row.Bid.Id,
            ScheduledDate = scheduledDate,
            ScheduledTime = scheduledTime,
            InterviewType = string.IsNullOrWhiteSpace(interviewType) ? Models.InterviewTypes.Interview : interviewType,
            Recruiter = recruiter,
            MeetingLink = meetingLink,
            Company = row.Bid.Company,
            Role = row.Bid.Role,
            ResumeId = row.Bid.ResumeId,
            AttachedJobDescription = jd,
            Status = Models.InterviewStatuses.Scheduled,
            Origin = "BidBoard"
        });
        StatusMessage = $"Interview scheduled for {row.Bid.Company} · {row.Bid.Role}.";
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            // Range when in custom mode, otherwise just the single SelectedDay.
            var fromDay = UseCustomRange ? From.Date : SelectedDay.Date;
            var toDay = UseCustomRange ? To.Date : SelectedDay.Date;
            if (toDay < fromDay) toDay = fromDay;
            var fromUtc = new DateTime(fromDay.Year, fromDay.Month, fromDay.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var toUtc = new DateTime(toDay.Year, toDay.Month, toDay.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().AddDays(1);
            var rows = await _service.BuildAsync(fromUtc, toUtc);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            StatusMessage = $"{rows.Count} row{(rows.Count == 1 ? "" : "s")}.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task AddLinkAsync()
    {
        var url = (NewLinkUrl ?? "").Trim();
        if (url.Length == 0) return;
        await _service.AddLinkAsync(url, NewLinkSharedJd);
        NewLinkUrl = "";
        NewLinkSharedJd = "";
        await ReloadAsync();
    }

    /// <summary>
    /// Parameters arrive as <c>object?</c> on purpose: WPF passes <c>DependencyProperty.UnsetValue</c>
    /// (a <c>MS.Internal.NamedObject</c>) during early binding evaluation, and a strongly-typed
    /// <c>RelayCommand&lt;BoardRow&gt;</c> would throw <c>ArgumentException</c> in <c>CanExecute</c>.
    /// Casting inside the body sidesteps that.
    /// </summary>
    [RelayCommand]
    public async Task SaveBidAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null) return;
        await _service.UpsertBidAsync(row.Link.Id, b =>
        {
            if (row.Bid != null)
            {
                b.ResumeId = row.Bid.ResumeId;
                b.Company = row.Bid.Company;
                b.Role = row.Bid.Role;
                b.PrimaryStacks = row.Bid.PrimaryStacks;
                b.Status = string.IsNullOrEmpty(row.Bid.Status) ? BidStatuses.Draft : row.Bid.Status;
                b.Origin = row.Bid.Origin;
                b.JobDescription = row.Bid.JobDescription;
                b.GptResumeContent = row.Bid.GptResumeContent;
                b.Comment = row.Bid.Comment;
            }
        });
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task DeleteBidAsync(object? param)
    {
        if (param is not BoardRow row || row.Bid == null) return;
        await _service.DeleteBidAsync(row.Bid.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task ToggleUselessAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null) return;
        await _service.SetUselessAsync(row.Link.Id, row.Link.MarkedUselessAt == null);
        await ReloadAsync();
    }

    /// <summary>
    /// Parse the row's manually-typed fast-feed line and apply it: sets resumeId/company/role/
    /// stacks on the bid and flips status to <c>applied</c>. Mirrors what the extension does
    /// for an auto-fed line, just driven by hand. The draft buffer is cleared after a
    /// successful save so the same line isn't reapplied on the next click.
    /// </summary>
    [RelayCommand]
    public async Task ApplyFastFeedAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null)
        {
            StatusMessage = "Pick a row first.";
            return;
        }
        var parsed = Services.FastFeed.ParseLine(row.FastFeedDraft);
        if (parsed == null)
        {
            StatusMessage = "Fast feed needs at least: UID, Company, Role";
            return;
        }
        await _service.UpsertBidAsync(row.Link.Id, b =>
        {
            b.ResumeId = parsed.ResumeId;
            b.Company = parsed.Company;
            b.Role = parsed.Role;
            b.PrimaryStacks = parsed.PrimaryStacks.ToList();
            b.Status = BidStatuses.Applied;
        });
        row.FastFeedDraft = "";
        StatusMessage = $"Applied: {parsed.Company} · {parsed.Role}";
        await ReloadAsync();
    }
}
