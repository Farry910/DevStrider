using System.Collections.ObjectModel;
using System.Windows;
using DevStrider.Desktop.Models;

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
        await MigrateResumeOutputSettingsAsync(list, s);

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

    /// <summary>
    /// Hands the resume output root, file base and salary answer over to the profiles that now own
    /// them, once.
    ///
    /// <para>
    /// They used to be machine-wide, which was wrong: each profile drives its own Word document, so
    /// the folder that document writes into and the file base it saves under belong to that profile,
    /// as its macro name and .docm already did. Moving the setting without moving the value would
    /// have quietly emptied a working configuration, so the old values are copied to every profile
    /// that has none and then cleared, leaving exactly one home for each.
    /// </para>
    /// </summary>
    private async Task MigrateResumeOutputSettingsAsync(IReadOnlyList<Profile> profiles, AppSettings settings)
    {
        var root = (settings.ResumeOutputRoot ?? "").Trim();
        var fileBase = (settings.ResumeOutputFileBase ?? "").Trim();
        var salary = (settings.SalaryExpectation ?? "").Trim();
        if (root.Length == 0 && fileBase.Length == 0 && salary.Length == 0) return;

        var moved = 0;
        foreach (var profile in profiles)
        {
            var changed = false;
            if (root.Length > 0 && string.IsNullOrWhiteSpace(profile.ResumeOutputRoot))
            {
                profile.ResumeOutputRoot = root;
                changed = true;
            }
            if (fileBase.Length > 0 && string.IsNullOrWhiteSpace(profile.ResumeOutputFileBase))
            {
                profile.ResumeOutputFileBase = fileBase;
                changed = true;
            }
            if (salary.Length > 0 && string.IsNullOrWhiteSpace(profile.SalaryExpectation))
            {
                profile.SalaryExpectation = salary;
                changed = true;
            }
            if (!changed) continue;
            await _profiles.UpdateAsync(profile);
            moved++;
        }

        // Cleared whether or not anything took them, so this runs once. A profile that already had
        // its own values keeps them; that is why each field is only filled when empty.
        var edit = await _settings.GetForEditAsync();
        edit.ResumeOutputRoot = "";
        edit.ResumeOutputFileBase = "";
        edit.SalaryExpectation = "";
        await _settings.SaveAsync(edit);
        if (moved > 0)
            System.Diagnostics.Debug.WriteLine($"[ProfileContext] resume output settings moved onto {moved} profile(s)");
    }

    public async Task SwitchAsync(ObjectId profileId)
    {
        if (Current?.Id == profileId) return;
        var target = All.FirstOrDefault(p => p.Id == profileId);
        if (target == null) return;

        var s = await _settings.GetForEditAsync();
        s.ActiveProfileId = target.Id;
        await _settings.SaveAsync(s);

        await RaiseOnUiAsync(() =>
        {
            Current = target;
            ProfileChanged?.Invoke();
        });
    }

    /// <summary>Pulls the latest profile list from the database (no active-profile switch).</summary>
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
