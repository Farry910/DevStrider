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
    private readonly Data.IPeerDirectory _peers;

    public ObservableCollection<Profile> Profiles => _context.All;

    private Profile? _selected;
    public Profile? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            SyncCallerDisplay();
        }
    }

    /// <summary>
    /// The selected profile's caller, as a name — read-only.
    ///
    /// <para>
    /// Allocating a caller is hr-system's job (Bidding &amp; Calling → Callers → Assignments): it
    /// is a decision across people, made by a lead or above, and the person chosen frequently has
    /// no DevStrider login to make it from. This app reads the field, resolves it to a name, and
    /// never writes it — see <c>HttpProfileRepository.UpsertAsync</c>, which leaves it off the
    /// wire entirely.
    /// </para>
    /// </summary>
    private string _callerDisplay = "";
    public string CallerDisplay { get => _callerDisplay; private set => SetProperty(ref _callerDisplay, value); }

    /// <summary>
    /// <c>app_user.id</c> → display name, built from the team directory so a caller id can be
    /// shown as a person. Only ever read.
    /// </summary>
    private readonly Dictionary<long, string> _callerNames = new();

    private string _newProfileName = "";
    public string NewProfileName { get => _newProfileName; set => SetProperty(ref _newProfileName, value); }

    public ProfilesViewModel(
        ProfilesService service,
        ProfileContext context,
        ActivityLogService activity,
        Data.IPeerDirectory peers)
    {
        _service = service;
        _context = context;
        _activity = activity;
        _peers = peers;
        Selected = _context.Current;
        _ = LoadCallersAsync();
        _context.ProfileListChanged += () => OnPropertyChanged(nameof(Profiles));
        _context.ProfileChanged += () =>
        {
            // If the active profile changed externally (title-bar switcher), reflect it here.
            if (Selected?.Id != _context.Current?.Id) Selected = _context.Current;
        };
    }

    /// <summary>
    /// Learn who the team's caller ids belong to, so a number can be shown as a name. Best-effort:
    /// a profile screen that won't open because the directory is unreachable would be a worse
    /// failure than one showing an id, so a failure here falls back to the id and says nothing.
    /// </summary>
    private async Task LoadCallersAsync()
    {
        try
        {
            var identities = await _peers.ListIdentitiesAsync();
            var names = identities
                .GroupBy(i => i.UserId)
                .ToDictionary(g => g.Key, g => g.First().Username);

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Fill()
            {
                _callerNames.Clear();
                foreach (var (id, name) in names) _callerNames[id] = name;
                SyncCallerDisplay();
            }
            if (dispatcher == null || dispatcher.CheckAccess()) Fill();
            else await dispatcher.InvokeAsync(Fill);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Profiles] caller names unavailable: {ex.Message}");
            SyncCallerDisplay();
        }
    }

    /// <summary>
    /// Show the selected profile's caller. Falls back to the raw id when the directory hasn't
    /// loaded or the caller has no DevStrider account — an id is still an answer, and pretending
    /// there is no caller would be a wrong one.
    /// </summary>
    private void SyncCallerDisplay()
    {
        if (Selected?.CallerId is not { } id)
        {
            CallerDisplay = "Not assigned — set in hr-system";
            return;
        }
        CallerDisplay = _callerNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"Account {id}";
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
        Profile created;
        try
        {
            created = await _service.CreateAsync(name);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't create '{name}': {ex.Message}";
            _activity.Error("Profiles", "Profile create failed", ex.Message);
            return;
        }

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

        // A failed save here used to escape into the dispatcher and come back as the fatal
        // "Dispatcher exception" box — for a save, which is the most ordinary thing this tab does.
        // Failures belong in the status line.
        try
        {
            await _service.UpdateAsync(saved);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save '{savedName}': {ex.Message}";
            _activity.Error("Profiles", "Profile save failed", ex.Message);
            return;
        }

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

    /// <summary>
    /// Default resume prompt. Every marker here is load-bearing: the Word macro looks up each
    /// <c>[Section]:</c> label and drops its text into the matching bookmark, so a missing label
    /// means that part of the template comes back blank. <c>[FolderName]:</c> names the output
    /// folder, and the final comma-separated line is what DevStrider parses to fill in the bid's
    /// company, role, and stacks.
    /// </summary>
    public const string DefaultResumePrompt =
        "Act as an expert resume writer. Tailor my resume to the job description below.\n" +
        "\n" +
        "Output ONLY the sections listed here, each starting with its exact label on its own line,\n" +
        "in this order and with no extra commentary:\n" +
        "\n" +
        "[Title]: <job title to show at the top of the resume>\n" +
        "[Summary]: <3-4 sentence professional summary aimed at this role>\n" +
        "[Skills]: <comma-separated skills, most relevant first>\n" +
        "[Subtitle 1]: <most recent job title — Company — dates>\n" +
        "[Experience 1]: <3-5 bullet lines for that role, tailored to the job description>\n" +
        "[Subtitle 2]: <second job title — Company — dates>\n" +
        "[Experience 2]: <3-5 bullet lines>\n" +
        "[Subtitle 3]: <third job title — Company — dates>\n" +
        "[Experience 3]: <3-5 bullet lines>\n" +
        "[FolderName]: <UID>, <Company>, <Role>, <Stack1>, <Stack2>, <Stack3>\n" +
        "<UID>, <Company>, <Role>, <Stack1>, <Stack2>, <Stack3>\n" +
        "\n" +
        "Rules:\n" +
        "- <UID> is a 5-character alphanumeric id you invent for this resume. Use the SAME UID on\n" +
        "  the [FolderName] line and the final line.\n" +
        "- <Company> and <Role> come from the job description; stacks are the 3-6 most important\n" +
        "  technologies it names.\n" +
        "- The last line must be the bare comma-separated line with no label and nothing after it.\n" +
        "- Wrap emphasis in **double asterisks**; it becomes bold in the document.";

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
        if (counts.bids + counts.interviews > 0)
        {
            ConfirmDialog.Ask(
                System.Windows.Application.Current?.MainWindow,
                $"'{Selected.Name}' isn't empty",
                $"This profile owns {counts.bids} bids and {counts.interviews} interviews. " +
                "Delete those first, then come back here.",
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
