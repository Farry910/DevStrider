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
}
