using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class ProfileViewModel : ViewModelBase
{
    private readonly ProfileService _service;
    public ObservableCollection<Education> Education { get; } = new();
    public ObservableCollection<Certification> Certifications { get; } = new();
    public ObservableCollection<Experience> Experiences { get; } = new();

    private UserProfile _profile = new();
    public UserProfile Profile
    {
        get => _profile;
        set => SetProperty(ref _profile, value);
    }

    public ProfileViewModel(ProfileService service)
    {
        _service = service;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Profile = await _service.GetAsync();
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
        Profile.Education = Education.ToList();
        Profile.Certifications = Certifications.ToList();
        Profile.Experiences = Experiences.ToList();
        await _service.SaveAsync(Profile);
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
