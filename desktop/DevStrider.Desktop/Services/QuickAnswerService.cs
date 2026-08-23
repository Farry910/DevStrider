using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The live list of questions an application left unanswered — what neither the profile, the
/// personal facts, nor ChatGPT could supply.
///
/// <para>
/// A singleton rather than state on a view-model, because the two ends are in different tabs: the
/// job browser publishes to it the moment a fill finishes, and the Quick answers tab is bound to
/// the same collection, so the questions appear while the user is still looking at the form. An
/// answer given there is written to the profile's personal facts, which is what makes it fill
/// itself the next time the same question comes up.
/// </para>
/// </summary>
public sealed class QuickAnswerService
{
    /// <summary>Bound directly by the Quick answers tab. Only ever touched on the UI thread.</summary>
    public ObservableCollection<PendingQuestion> Pending { get; } = new();

    public event Action? Changed;

    /// <summary>
    /// Adds whatever this page still wants. Questions already waiting are left as they are, so a
    /// half-typed answer survives a re-fill of the same form.
    /// </summary>
    public void Publish(string site, IEnumerable<string> questions)
    {
        var added = false;
        foreach (var raw in questions)
        {
            var question = Clean(raw);
            if (question.Length == 0) continue;
            if (Pending.Any(item => string.Equals(item.Question, question, StringComparison.OrdinalIgnoreCase)))
                continue;
            Pending.Add(new PendingQuestion { Site = site ?? "", Question = question });
            added = true;
        }
        if (added) Changed?.Invoke();
    }

    public void Remove(PendingQuestion question)
    {
        if (Pending.Remove(question)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (Pending.Count == 0) return;
        Pending.Clear();
        Changed?.Invoke();
    }

    /// <summary>The fill script tags dropdowns for the review line; the question is the key.</summary>
    private static string Clean(string? raw)
    {
        var text = (raw ?? "").Trim();
        return text.EndsWith(" (dropdown)", StringComparison.OrdinalIgnoreCase) ? text[..^11].Trim() : text;
    }
}

public sealed partial class PendingQuestion : ObservableObject
{
    public string Site { get; set; } = "";
    public string Question { get; set; } = "";

    [ObservableProperty] private string _answer = "";
}
