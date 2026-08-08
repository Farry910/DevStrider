using System.Collections.ObjectModel;
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
        ActivityLogService activity)
    {
        _service = service;
        _context = context;
        _activity = activity;
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
        // Capture everything needed *before* the refresh. RefreshListAsync clears the bound
        // collection, and the profile ComboBox binds SelectedItem TwoWay — so WPF pushes null
        // straight back into Selected while the clear is in flight. Touching Selected.Name
        // afterwards threw a NullReferenceException on every successful save, which surfaced as
        // the "Dispatcher exception" dialog right after picking a .docm.
        var saved = Selected;
        var savedId = saved.Id;
        var savedName = saved.Name;

        await _service.UpdateAsync(saved);
        await _context.RefreshListAsync();

        // Re-point at the fresh instance the refresh produced, so the editor stays populated
        // instead of blanking out.
        Selected = _context.All.FirstOrDefault(p => p.Id == savedId) ?? _context.Current;

        StatusMessage = $"Saved profile '{savedName}'.";
        _activity.Success("Profiles", "Profile saved", savedName);
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

    [RelayCommand]
    public async Task SetActiveAsync()
    {
        if (Selected == null) return;
        // Same capture-first rule as SaveProfileAsync: the switch fires ProfileChanged, and
        // subscribers can reassign Selected out from under us.
        var name = Selected.Name;
        await _context.SwitchAsync(Selected.Id);
        StatusMessage = $"Switched to '{name}'.";
        _activity.Success("Profiles", "Switched profile", name);
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
}
