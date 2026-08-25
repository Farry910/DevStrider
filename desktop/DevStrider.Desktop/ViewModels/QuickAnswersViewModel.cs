using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The Quick answers tab: questions the current application could not answer, waiting for a human,
/// live while the form is still on screen in the job browser.
///
/// <para>
/// An answer saved here becomes a custom fact on the active profile, so it is reference data from
/// that moment on — the same question on the next application fills itself, and ChatGPT sees it
/// when reasoning about anything related.
/// </para>
/// </summary>
public sealed partial class QuickAnswersViewModel : ViewModelBase
{
    private readonly QuickAnswerService _questions;
    private readonly PersonFactsService _person;
    private readonly ProfileContext _profiles;

    public ObservableCollection<PendingQuestion> Pending => _questions.Pending;

    public QuickAnswersViewModel(QuickAnswerService questions, PersonFactsService person, ProfileContext profiles)
    {
        _questions = questions;
        _person = person;
        _profiles = profiles;
        _questions.Changed += () =>
        {
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasPending));
        };
    }

    public bool HasPending => Pending.Count > 0;

    public string Summary => Pending.Count == 0
        ? "Nothing waiting. Questions appear here the moment an application leaves one unanswered."
        : $"{Pending.Count} question(s) this application could not answer. Answering one saves it to "
          + "the profile's personal info, so it fills itself next time.";

    [RelayCommand]
    private async Task SaveAsync(PendingQuestion? question)
    {
        if (question == null) return;
        if (string.IsNullOrWhiteSpace(question.Answer))
        {
            StatusMessage = "Type an answer first.";
            return;
        }
        var profile = _profiles.Current;
        if (profile == null) { StatusMessage = "No active profile."; return; }
        try
        {
            await _person.AddCustomAsync(profile.Id, question.Question, question.Answer);
            _questions.Remove(question);
            StatusMessage = $"Saved. \"{question.Question}\" is now part of {profile.Name}'s personal info.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't save that answer: " + Safe.Redact(ex.Message);
        }
    }

    /// <summary>Drops a question without answering — the ones that are genuinely one-offs.</summary>
    [RelayCommand]
    private void Dismiss(PendingQuestion? question)
    {
        if (question != null) _questions.Remove(question);
    }

    [RelayCommand]
    private void ClearAll() => _questions.Clear();
}
