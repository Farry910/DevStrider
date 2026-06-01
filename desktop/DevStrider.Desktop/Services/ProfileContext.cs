using System.Collections.ObjectModel;
using System.Windows;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// In-memory cache of the currently active <see cref="Profile"/> + the full list of profiles
/// for UI binding. Fires <see cref="ProfileChanged"/> when the active profile flips so VMs
/// can reload. Everything that needs to know "whose data should I show?" goes through here.
///
/// <para>
/// Initialise via <see cref="InitAsync"/> at app startup (after migration runs). Single instance
/// per process, registered as a DI singleton.
/// </para>
/// </summary>
public sealed class ProfileContext
{
    private readonly ProfilesService _profiles;
    private readonly SettingsService _settings;

    public ObservableCollection<Profile> All { get; } = new();
    public Profile? Current { get; private set; }

    /// <summary>Fires (on the UI thread) after <see cref="Current"/> changes.</summary>
    public event Action? ProfileChanged;

    /// <summary>Fires (on the UI thread) after <see cref="All"/> gains/loses an entry.</summary>
    public event Action? ProfileListChanged;

    public ProfileContext(ProfilesService profiles, SettingsService settings)
    {
        _profiles = profiles;
        _settings = settings;
    }

    /// <summary>Load profiles + resolve active. Idempotent; safe to call again after structural changes.</summary>
    public async Task InitAsync()
    {
        var list = await _profiles.ListAsync();
        var s = await _settings.GetAsync();

        var active = list.FirstOrDefault(p => p.Id == s.ActiveProfileId)
                  ?? list.FirstOrDefault();

        await RaiseOnUiAsync(() =>
        {
            All.Clear();
            foreach (var p in list) All.Add(p);
            Current = active;
            ProfileListChanged?.Invoke();
            ProfileChanged?.Invoke();
        });
    }

    /// <summary>Switch active profile. Persists <see cref="AppSettings.ActiveProfileId"/> and broadcasts.</summary>
    public async Task SwitchAsync(ObjectId profileId)
    {
        if (Current?.Id == profileId) return;
        var target = All.FirstOrDefault(p => p.Id == profileId);
        if (target == null) return;

        var s = await _settings.GetAsync();
        s.ActiveProfileId = target.Id;
        await _settings.SaveAsync(s);

        await RaiseOnUiAsync(() =>
        {
            Current = target;
            ProfileChanged?.Invoke();
        });
    }

    /// <summary>Pulls the latest profile list from Mongo (no active-profile switch).</summary>
    public async Task RefreshListAsync()
    {
        var list = await _profiles.ListAsync();
        var currentId = Current?.Id;
        await RaiseOnUiAsync(() =>
        {
            All.Clear();
            foreach (var p in list) All.Add(p);
            // If the current profile was renamed / edited, swap in the fresh instance so
            // bindings to its properties (Name, WordDocPath) update.
            if (currentId.HasValue)
                Current = All.FirstOrDefault(p => p.Id == currentId.Value) ?? All.FirstOrDefault();
            ProfileListChanged?.Invoke();
        });
    }

    private static Task RaiseOnUiAsync(Action body)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            body();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(body).Task;
    }
}
