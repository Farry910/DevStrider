using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

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
        await _service.DeleteAsync(iv.Id);
        await ReloadAsync();
    }
}
