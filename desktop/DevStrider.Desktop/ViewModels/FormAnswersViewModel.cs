using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The Answers tab of Job Operations: every question the app has met, what it answered, and where
/// that answer came from.
///
/// <para>
/// Approving is the point. Starting the automatic flow is a decision to trust ChatGPT's answers for
/// that run, so they are applied immediately and land here unapproved; this list is where they are
/// read, corrected and promoted. The coverage line says how much of the bank the user actually
/// stands behind, which is the number that matters before sending applications out.
/// </para>
/// </summary>
public sealed partial class FormAnswersViewModel : ViewModelBase
{
    private readonly FormAnswerService _answers;
    private readonly ProfileContext _profiles;

    public ObservableCollection<FormAnswer> Answers { get; } = new();

    public FormAnswersViewModel(FormAnswerService answers, ProfileContext profiles)
    {
        _answers = answers;
        _profiles = profiles;
        _profiles.ProfileChanged += () => _ = LoadAsync();
        _ = LoadAsync();
    }

    public int OutstandingCount => Answers.Count(a => a.IsOutstanding);
    public int AwaitingApprovalCount => Answers.Count(a => a.NeedsApproval);
    public int ApprovedCount => Answers.Count(a => a.IsApproved);
    public bool HasPending => AwaitingApprovalCount > 0;

    public string Coverage
    {
        get
        {
            var answered = Answers.Count(a => !a.IsOutstanding);
            if (answered == 0 && OutstandingCount == 0) return "No application questions recorded yet.";
            var parts = new List<string> { $"{ApprovedCount} of {answered} answers approved" };
            if (AwaitingApprovalCount > 0) parts.Add($"{AwaitingApprovalCount} from ChatGPT awaiting review");
            if (OutstandingCount > 0) parts.Add($"{OutstandingCount} still need an answer from you");
            return string.Join(", ", parts) + ".";
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        try
        {
            var rows = await _answers.ListAsync(profile.Id);
            Answers.Clear();
            foreach (var row in rows) Answers.Add(row);
            NotifyCounts();
            StatusMessage = Coverage;
        }
        catch (Exception ex) { StatusMessage = "Could not load saved answers: " + ex.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync(FormAnswer? answer)
    {
        if (answer == null) return;
        try
        {
            await _answers.SaveAsync(answer);
            NotifyCounts();
            StatusMessage = $"Saved. {Coverage}";
        }
        catch (Exception ex) { StatusMessage = "Could not save that answer: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ApproveAsync(FormAnswer? answer)
    {
        if (answer == null || answer.IsOutstanding) return;
        try
        {
            await _answers.ApproveAsync(answer);
            NotifyCounts();
            StatusMessage = $"Approved. {Coverage}";
        }
        catch (Exception ex) { StatusMessage = "Could not approve that answer: " + ex.Message; }
    }

    /// <summary>Approves every ChatGPT answer that has one. Outstanding rows are left alone.</summary>
    [RelayCommand]
    private async Task ApproveAllAsync()
    {
        var pending = Answers.Where(a => a.NeedsApproval).ToList();
        if (pending.Count == 0) { StatusMessage = "Nothing is waiting for approval."; return; }
        try
        {
            foreach (var answer in pending) await _answers.ApproveAsync(answer);
            NotifyCounts();
            StatusMessage = $"Approved {pending.Count} answer(s). {Coverage}";
        }
        catch (Exception ex) { StatusMessage = "Could not approve those answers: " + ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteAsync(FormAnswer? answer)
    {
        if (answer == null) return;
        try
        {
            await _answers.DeleteAsync(answer);
            Answers.Remove(answer);
            NotifyCounts();
            StatusMessage = $"Removed. {Coverage}";
        }
        catch (Exception ex) { StatusMessage = "Could not remove that answer: " + ex.Message; }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(OutstandingCount));
        OnPropertyChanged(nameof(AwaitingApprovalCount));
        OnPropertyChanged(nameof(ApprovedCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(Coverage));
    }
}
