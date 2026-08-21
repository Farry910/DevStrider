using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Bson;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Receives proposals the user copied from ChatGPT after ChatGPT consulted connected Gmail and
/// Calendar. No ChatGPT account token enters DevStrider; every database write remains visible and
/// requires the user to select and apply it here.
/// </summary>
public sealed partial class AssistedAutomationViewModel : ViewModelBase
{
    private readonly IBidRepository _bidRepository;
    private readonly BidBoardService _bids;
    private readonly InterviewService _interviews;
    private readonly ProfileContext _profiles;
    private readonly ActivityLogService _activity;

    private string _proposalJson = "";
    public string ProposalJson { get => _proposalJson; set => SetProperty(ref _proposalJson, value); }

    public ObservableCollection<AssistedActionRow> Actions { get; } = new();

    public AssistedAutomationViewModel(
        IBidRepository bidRepository,
        BidBoardService bids,
        InterviewService interviews,
        ProfileContext profiles,
        ActivityLogService activity)
    {
        _bidRepository = bidRepository;
        _bids = bids;
        _interviews = interviews;
        _profiles = profiles;
        _activity = activity;
        _profiles.ProfileChanged += () => Actions.Clear();
    }

    [RelayCommand]
    private void CopyReviewPrompt()
    {
        const string prompt = """
Review my connected Gmail and Google Calendar for job-application updates. Return ONLY valid JSON.
Use this exact shape:
{"actions":[{"type":"mark_bid_rejected|create_interview","company":"","role":"","scheduled_at":"ISO-8601 optional","interview_type":"HR|Assessment|Phone Call|Tech 1|Tech 2|Tech 3|Client Interview|Final Interview|Offer optional","meeting_link":"optional","evidence":"sender, subject, and date"}]}
Only propose an action when the company and role are explicit in the source. Never invent facts.
""";
        System.Windows.Clipboard.SetText(prompt);
        StatusMessage = "Review prompt copied. Paste it into ChatGPT, then paste ChatGPT's JSON reply here.";
    }

    [RelayCommand]
    private async Task ReviewAsync()
    {
        Actions.Clear();
        var profileId = _profiles.Current?.Id ?? ObjectId.Empty;
        if (profileId == ObjectId.Empty) { StatusMessage = "No active profile."; return; }
        try
        {
            using var document = JsonDocument.Parse(ProposalJson);
            if (!document.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected an 'actions' array.");
            if (actions.GetArrayLength() > 100)
                throw new JsonException("At most 100 proposed actions can be reviewed at once.");
            var bids = await _bidRepository.ListByProfileAsync(profileId);
            foreach (var item in actions.EnumerateArray())
                Actions.Add(CreateRow(item, bids));
            StatusMessage = $"{Actions.Count} proposed action(s). Select only the verified items, then apply.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            StatusMessage = "Couldn't read ChatGPT's proposal: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        var selected = Actions.Where(a => a.IsSelected && a.CanApply).ToList();
        if (selected.Count == 0) { StatusMessage = "Select at least one matched, valid action."; return; }
        IsBusy = true;
        try
        {
            foreach (var action in selected)
            {
                if (action.Type == "mark_bid_rejected" && action.BidId is ObjectId bidId)
                {
                    await _bids.UpdateAsync(bidId, bid => bid.Status = BidStatuses.Rejected);
                }
                else if (action.Type == "create_interview" && action.BidId is ObjectId sourceBid)
                {
                    await _interviews.CreateAsync(new Interview
                    {
                        BidId = sourceBid,
                        Company = action.Company,
                        Role = action.Role,
                        InterviewType = action.InterviewType,
                        MeetingLink = action.MeetingLink,
                        ScheduledDate = action.ScheduledAt?.Date,
                        ScheduledTime = action.ScheduledAt?.ToString("HH:mm") ?? "",
                        Status = InterviewStatuses.Scheduled,
                        UserComment = "Assisted automation evidence: " + action.Evidence,
                    });
                }
                else
                {
                    continue;
                }
                action.Applied = true;
                action.IsSelected = false;
                _activity.Success("Assisted automation", "Action applied", action.Summary);
            }
            StatusMessage = "Selected actions applied and recorded in Activity.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't apply actions: " + SharedDbCredentials.Redact(ex.Message);
            _activity.Error("Assisted automation", "Action apply failed", SharedDbCredentials.Redact(ex.Message));
        }
        finally { IsBusy = false; }
    }

    private static AssistedActionRow CreateRow(JsonElement item, List<UserBid> bids)
    {
        static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        var type = Text(item, "type");
        var company = Text(item, "company");
        var role = Text(item, "role");
        var matches = string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(role)
            ? new List<UserBid>()
            : bids.Where(b => string.Equals(b.Company.Trim(), company, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(b.Role.Trim(), role, StringComparison.OrdinalIgnoreCase)).ToList();
        var row = new AssistedActionRow
        {
            Type = type,
            Company = company,
            Role = role,
            InterviewType = Text(item, "interview_type"),
            MeetingLink = Text(item, "meeting_link"),
            Evidence = Text(item, "evidence"),
            BidId = matches.Count == 1 ? matches[0].Id : null,
            MatchMessage = matches.Count switch
            {
                1 => "Matched one bid",
                0 => "No exact company + role bid match",
                _ => "Multiple matching bids — resolve manually",
            }
        };
        if (DateTimeOffset.TryParse(Text(item, "scheduled_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var scheduled)) row.ScheduledAt = scheduled;
        row.IsSupported = type is "mark_bid_rejected" or "create_interview";
        if (type == "create_interview" && string.IsNullOrWhiteSpace(row.InterviewType)) row.InterviewType = InterviewTypes.HR;
        if (!row.IsSupported) row.MatchMessage = "Unsupported action type";
        else if (string.IsNullOrWhiteSpace(company)) { row.IsSupported = false; row.MatchMessage = "Company is required"; }
        else if (string.IsNullOrWhiteSpace(role)) { row.IsSupported = false; row.MatchMessage = "Role is required"; }
        else if (string.IsNullOrWhiteSpace(row.Evidence)) { row.IsSupported = false; row.MatchMessage = "Source evidence is required"; }
        else if (type == "create_interview")
        {
            var canonicalType = InterviewTypes.All.FirstOrDefault(candidate =>
                string.Equals(candidate, row.InterviewType, StringComparison.OrdinalIgnoreCase));
            if (canonicalType == null) { row.IsSupported = false; row.MatchMessage = "Unsupported interview type"; }
            else row.InterviewType = canonicalType;
        }
        return row;
    }
}

public sealed partial class AssistedActionRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _applied;
    public string Type { get; init; } = "";
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string InterviewType { get; set; } = "";
    public string MeetingLink { get; init; } = "";
    public string Evidence { get; init; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
    public ObjectId? BidId { get; init; }
    public bool IsSupported { get; set; }
    public string MatchMessage { get; set; } = "";
    public bool CanApply => IsSupported && BidId != null && !Applied;
    partial void OnAppliedChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    public string Summary => $"{Type}: {Company} · {Role}";
}
