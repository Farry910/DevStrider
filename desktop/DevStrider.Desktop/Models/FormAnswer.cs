using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One reusable answer to one application question, in <c>ds_form_answers</c>.
///
/// <para>
/// Keyed on <see cref="FieldKey"/> — the normalised question text — rather than the control's name,
/// because the same question appears as <c>question_7295875009</c> on one board and something else
/// entirely on the next. Answers are deliberately not scoped by site: an answer given once is the
/// same answer everywhere, and ChatGPT bridges the difference in wording.
/// </para>
///
/// <para>
/// An empty <see cref="Answer"/> means the question was met and left outstanding, which is what
/// puts it in front of the user in Job Operations.
/// </para>
/// </summary>
public sealed partial class FormAnswer : ObservableObject
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public long UserId { get; set; }
    public ObjectId ProfileId { get; set; }

    /// <summary>Normalised question text. The match key, and unique per profile.</summary>
    public string FieldKey { get; set; } = "";

    /// <summary>The control's name or id, shown so a field is identifiable on the page.</summary>
    public string FieldName { get; set; } = "";

    /// <summary>The question exactly as the form words it.</summary>
    public string Question { get; set; } = "";

    [ObservableProperty] private string _answer = "";

    /// <summary>text | choice | boolean. Choice answers are comma-separated.</summary>
    public string Kind { get; set; } = FormAnswerKinds.Text;

    [ObservableProperty] private string _source = FormAnswerSources.Gpt;

    /// <summary>Null while a ChatGPT answer is still waiting to be approved.</summary>
    [ObservableProperty] private DateTime? _approvedAt;

    /// <summary>Most recent host the question was seen on. Context for the user, never a key.</summary>
    public string LastSite { get; set; } = "";

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public int SeenCount { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsApproved => ApprovedAt.HasValue;
    public bool IsOutstanding => string.IsNullOrWhiteSpace(Answer);
    public bool NeedsApproval => !IsOutstanding && !IsApproved;

    /// <summary>
    /// The normalisation both the fill scripts and this table agree on, so a question answered on
    /// one board matches the same question on the next. Mirrors <c>norm()</c> in the page scripts.
    /// </summary>
    public static string Normalise(string? question) =>
        string.Join(' ', (question ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray().AsSpan().ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public static class FormAnswerKinds
{
    public const string Text = "text";
    public const string Choice = "choice";
    public const string Boolean = "boolean";
}

public static class FormAnswerSources
{
    public const string Gpt = "gpt";
    public const string User = "user";
}
