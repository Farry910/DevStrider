using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.ViewModels;

/// <summary>Shared base — CommunityToolkit.Mvvm already gives us property change notification.</summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
}
