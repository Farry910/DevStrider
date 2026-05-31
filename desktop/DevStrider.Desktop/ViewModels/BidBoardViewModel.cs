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

    public ObservableCollection<BoardRow> Rows { get; } = new();

    private DateTime _selectedDay = DateTime.Today;
    public DateTime SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value)) _ = ReloadAsync();
        }
    }

    private string _newLinkUrl = "";
    public string NewLinkUrl { get => _newLinkUrl; set => SetProperty(ref _newLinkUrl, value); }

    private string _newLinkSharedJd = "";
    public string NewLinkSharedJd { get => _newLinkSharedJd; set => SetProperty(ref _newLinkSharedJd, value); }

    public BidBoardViewModel(BidBoardService service, ProfileService profiles)
    {
        _service = service;
        _profiles = profiles;
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var from = new DateTime(SelectedDay.Year, SelectedDay.Month, SelectedDay.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var to = from.AddDays(1);
            var rows = await _service.BuildAsync(from, to);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            StatusMessage = $"{rows.Count} rows.";
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

    [RelayCommand]
    public async Task SaveBidAsync(BoardRow row)
    {
        if (row?.Link == null) return;
        await _service.UpsertBidAsync(row.Link.Id, b =>
        {
            // The view-side bid object is mutated in-place by the form. Persist as-is.
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
    public async Task DeleteBidAsync(BoardRow row)
    {
        if (row?.Bid == null) return;
        await _service.DeleteBidAsync(row.Bid.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task ToggleUselessAsync(BoardRow row)
    {
        if (row?.Link == null) return;
        await _service.SetUselessAsync(row.Link.Id, row.Link.MarkedUselessAt == null);
        await ReloadAsync();
    }
}
