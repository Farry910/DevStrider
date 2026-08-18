using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The CV editor for the active bidding identity — education, certifications, experience.
///
/// <para>
/// These used to hang off the account. They belong to a <see cref="Profile"/> instead: a profile
/// <i>is</i> the person being bid for, and someone running three of them is bidding three
/// different CVs. The account keeps only what is per-person-behind-the-keyboard.
/// </para>
/// </summary>
public partial class ProfileViewModel : ViewModelBase
{
    private readonly ProfilesService _profiles;
    private readonly ProfileContext _context;

    public ObservableCollection<Education> Education { get; } = new();
    public ObservableCollection<Certification> Certifications { get; } = new();
    public ObservableCollection<Experience> Experiences { get; } = new();

    private Profile _profile = new();
    public Profile Profile
    {
        get => _profile;
        set => SetProperty(ref _profile, value);
    }

    public ProfileViewModel(ProfilesService profiles, ProfileContext context)
    {
        _profiles = profiles;
        _context = context;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var active = _context.Current;
            if (active == null)
            {
                StatusMessage = "No active profile.";
                return;
            }
            // Re-read rather than binding the cached instance: the CV rows are loaded with the
            // profile, and the switcher's copy is only as fresh as the last list refresh.
            Profile = await _profiles.GetAsync(active.Id) ?? active;

            Education.Clear();
            foreach (var e in Profile.Education) Education.Add(e);
            Certifications.Clear();
            foreach (var c in Profile.Certifications) Certifications.Add(c);
            Experiences.Clear();
            foreach (var x in Profile.Experiences) Experiences.Add(x);
            StatusMessage = "Loaded.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        // List order is the CV's order and is persisted explicitly, so the collections are
        // written back whole — see IProfileRepository on why the CV tables are rewritten rather
        // than diffed.
        Profile.Education = Education.ToList();
        Profile.Certifications = Certifications.ToList();
        Profile.Experiences = Experiences.ToList();
        await _profiles.UpdateAsync(Profile);
        await _context.RefreshListAsync();
        StatusMessage = "Saved.";
    }

    /// <summary>
    /// Remove-* parameters are <c>object?</c> to tolerate WPF passing <c>UnsetValue</c> during
    /// early binding evaluation — see BidBoardViewModel for the same workaround.
    /// </summary>
    [RelayCommand] public void AddEducation() => Education.Add(new Education());
    [RelayCommand]
    public void RemoveEducation(object? param)
    {
        if (param is Education e) Education.Remove(e);
    }
    [RelayCommand] public void AddCertification() => Certifications.Add(new Certification());
    [RelayCommand]
    public void RemoveCertification(object? param)
    {
        if (param is Certification c) Certifications.Remove(c);
    }
    [RelayCommand] public void AddExperience() => Experiences.Add(new Experience());
    [RelayCommand]
    public void RemoveExperience(object? param)
    {
        if (param is Experience x) Experiences.Remove(x);
    }
}
