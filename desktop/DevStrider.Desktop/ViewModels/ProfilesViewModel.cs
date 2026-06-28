using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;
using MongoDB.Bson;

namespace DevStrider.Desktop.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly ProfilesService _service;
    private readonly ProfileContext _context;
    private readonly ActivityLogService _activity;
    private readonly RegistrySyncService _registrySync;

    public ObservableCollection<Profile> Profiles => _context.All;

    private Profile? _selected;
    public Profile? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    private string _newProfileName = "";
    public string NewProfileName { get => _newProfileName; set => SetProperty(ref _newProfileName, value); }

    public ProfilesViewModel(
        ProfilesService service,
        ProfileContext context,
        ActivityLogService activity,
        RegistrySyncService registrySync)
    {
        _service = service;
        _context = context;
        _activity = activity;
        _registrySync = registrySync;
        Selected = _context.Current;
        _context.ProfileListChanged += () => OnPropertyChanged(nameof(Profiles));
        _context.ProfileChanged += () =>
        {
            // If the active profile changed externally (title-bar switcher), reflect it here.
            if (Selected?.Id != _context.Current?.Id) Selected = _context.Current;
        };
    }

    [RelayCommand]
    public async Task CreateProfileAsync()
    {
        var name = (NewProfileName ?? "").Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Enter a profile name first.";
            return;
        }
        var created = await _service.CreateAsync(name);
        await _context.RefreshListAsync();
        Selected = _context.All.FirstOrDefault(p => p.Id == created.Id);
        NewProfileName = "";
        StatusMessage = $"Created profile '{created.Name}'.";
        _activity.Success("Profiles", "Profile created", created.Name);
    }

    [RelayCommand]
    public async Task SaveProfileAsync()
    {
        if (Selected == null)
        {
            StatusMessage = "Pick a profile first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Selected.Name))
        {
            StatusMessage = "Profile name can't be empty.";
            return;
        }
        await _service.UpdateAsync(Selected);
        await _context.RefreshListAsync();
        // If the saved profile is the active one, mirror its WordDocPath back to registry.
        if (Selected.Id == _context.Current?.Id) await _registrySync.PushAsync();
        StatusMessage = $"Saved profile '{Selected.Name}'.";
        _activity.Success("Profiles", "Profile saved", Selected.Name);
    }

    [RelayCommand]
    public async Task BrowseWordPathAsync()
    {
        if (Selected == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Word document for '{Selected.Name}'",
            Filter = "Word macro-enabled (*.docm)|*.docm|Word documents (*.docx)|*.docx|All files (*.*)|*.*",
            FilterIndex = 1,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() == true)
        {
            Selected.WordDocPath = dlg.FileName;
            OnPropertyChanged(nameof(Selected));
            await SaveProfileAsync();
        }
    }

    /// <summary>Default resume prompt that emits both markers the pipeline needs.</summary>
    public const string DefaultResumePrompt =
        "Act as an expert resume writer. Tailor my resume to the job description below.\n" +
        "\n" +
        "Output ONLY the finished resume text. After the resume, append exactly these two lines:\n" +
        "[FolderName]: <short_filename_for_this_company>\n" +
        "<UID>, <Company>, <Role>, <Stack1>, <Stack2>, <Stack3>\n" +
        "\n" +
        "Where <UID> is a 5-character id you invent for this resume, <Company> and <Role> come " +
        "from the job description, and the stacks are the 3-5 most important technologies.";

    [RelayCommand]
    public void UseDefaultPrompt()
    {
        if (Selected == null) return;
        Selected.ResumePrompt = DefaultResumePrompt;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>
    /// Import ResumeAuto's <c>profiles.json</c> (user picks the file). For each entry: read the
    /// <c>prompt_path</c> file's contents into <see cref="Profile.ResumePrompt"/>, copy
    /// <c>docm_path</c> → WordDocPath and <c>macro_name</c> → MacroName. Matches local profiles
    /// by name (case-insensitive); creates missing ones.
    /// </summary>
    [RelayCommand]
    public async Task ImportFromResumeAutoAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ResumeAuto profiles.json",
            Filter = "profiles.json|profiles.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FilterIndex = 1,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dlg.FileName);
            var baseDir = Path.GetDirectoryName(dlg.FileName) ?? "";
            var entries = JsonSerializer.Deserialize<List<ResumeAutoProfile>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            int created = 0, updated = 0;
            foreach (var e in entries)
            {
                var name = (e.Name ?? "").Trim();
                if (name.Length == 0 || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) continue;

                // Resolve + read the prompt file (relative paths resolved against profiles.json's folder).
                var prompt = "";
                var promptPath = (e.PromptPath ?? "").Trim();
                if (promptPath.Length > 0)
                {
                    if (!Path.IsPathRooted(promptPath)) promptPath = Path.Combine(baseDir, promptPath);
                    if (File.Exists(promptPath))
                        try { prompt = await File.ReadAllTextAsync(promptPath); } catch { /* leave blank */ }
                }

                var existing = _context.All.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = await _service.CreateAsync(name);
                    created++;
                }
                else updated++;

                existing.WordDocPath = (e.DocmPath ?? "").Trim();
                existing.MacroName = (e.MacroName ?? "").Trim();
                if (prompt.Length > 0) existing.ResumePrompt = prompt;
                await _service.UpdateAsync(existing);
            }

            await _context.RefreshListAsync();
            Selected = _context.Current;
            StatusMessage = $"Imported ResumeAuto profiles: {created} created, {updated} updated.";
            _activity.Success("Profiles", "ResumeAuto import", $"{created} created, {updated} updated.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            _activity.Error("Profiles", "ResumeAuto import failed", ex.Message);
        }
    }

    [RelayCommand]
    public async Task SetActiveAsync()
    {
        if (Selected == null) return;
        await _context.SwitchAsync(Selected.Id);
        await _registrySync.PushAsync();
        StatusMessage = $"Switched to '{Selected.Name}'.";
        _activity.Success("Profiles", "Switched profile", Selected.Name);
    }

    [RelayCommand]
    public async Task DeleteProfileAsync()
    {
        if (Selected == null) return;
        if (_context.All.Count <= 1)
        {
            ConfirmDialog.Ask(
                System.Windows.Application.Current?.MainWindow,
                "Can't delete the only profile",
                "DevStrider needs at least one profile to work. Create a second one first, then delete this.",
                okText: "OK", cancelText: "Close", danger: false);
            return;
        }

        var counts = await _service.OwnedRowCountsAsync(Selected.Id);
        if (counts.links + counts.bids + counts.interviews > 0)
        {
            ConfirmDialog.Ask(
                System.Windows.Application.Current?.MainWindow,
                $"'{Selected.Name}' isn't empty",
                $"This profile owns {counts.links} links, {counts.bids} bids, and {counts.interviews} interviews. " +
                "Delete those first (or reassign them by hand in Mongo), then come back here.",
                okText: "OK", cancelText: "Close", danger: false);
            return;
        }

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Delete profile?",
            $"{Selected.Name}\n\nThis profile has no bids or interviews. Removing it is permanent.");
        if (!ok) return;

        var deletedName = Selected.Name;
        var wasActive = Selected.Id == _context.Current?.Id;
        await _service.DeleteAsync(Selected.Id);
        await _context.RefreshListAsync();
        if (wasActive && _context.All.Count > 0)
            await _context.SwitchAsync(_context.All[0].Id);
        Selected = _context.Current;
        StatusMessage = $"Deleted profile '{deletedName}'.";
        _activity.Success("Profiles", "Profile deleted", deletedName);
    }

    /// <summary>Wire shape of one entry in ResumeAuto's profiles.json (snake_case keys).</summary>
    private sealed class ResumeAutoProfile
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("prompt_path")] public string? PromptPath { get; set; }
        [JsonPropertyName("docm_path")] public string? DocmPath { get; set; }
        [JsonPropertyName("macro_name")] public string? MacroName { get; set; }
    }
}
