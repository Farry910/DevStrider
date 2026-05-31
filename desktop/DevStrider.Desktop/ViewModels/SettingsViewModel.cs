using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;
    private readonly GitHubSyncService _sync;

    public SettingsViewModel(SettingsService settings, ProfileService profiles, GitHubSyncService sync)
    {
        _settings = settings;
        _profiles = profiles;
        _sync = sync;
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

    private string _username = "me";
    /// <summary>Mirror of <see cref="UserProfile.Username"/> — what your file is named in the team repo.</summary>
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    /// <summary>Plaintext PAT bound to the password box; protected on save.</summary>
    private string _githubTokenPlain = "";
    public string GitHubTokenPlain { get => _githubTokenPlain; set => SetProperty(ref _githubTokenPlain, value); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Model = await _settings.GetAsync();
            var profile = await _profiles.GetAsync();
            Username = profile.Username;
            GitHubTokenPlain = SecretStore.Unprotect(Model.GitHubTokenProtected);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            Model.GitHubTokenProtected = SecretStore.Protect(GitHubTokenPlain ?? "");
            await _settings.SaveAsync(Model);

            var p = await _profiles.GetAsync();
            p.Username = string.IsNullOrWhiteSpace(Username) ? "me" : Username.Trim();
            await _profiles.SaveAsync(p);

            StatusMessage = "Saved.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task PushTodayAsync()
    {
        IsBusy = true;
        try
        {
            var profile = await _profiles.GetAsync();
            await _sync.PushTodayAsync(profile.Username);
            StatusMessage = $"Pushed snapshot to GitHub ({profile.Username}.json under today).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Push failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
