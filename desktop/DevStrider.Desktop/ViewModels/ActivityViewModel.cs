using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class ActivityViewModel : ViewModelBase
{
    private readonly ActivityLogService _log;

    public ObservableCollection<ActivityEntry> Entries => _log.Entries;

    public ActivityViewModel(ActivityLogService log)
    {
        _log = log;
    }

    [RelayCommand]
    public void Clear() => _log.Clear();
}
