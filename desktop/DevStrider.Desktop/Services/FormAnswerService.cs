using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The reusable answer bank: what the user has told job forms before, and what ChatGPT proposed
/// and is still waiting to be approved.
///
/// <para>
/// This replaces <c>AppSettings.JobFormAnswers</c>, which held the same thing in settings.json and
/// therefore only on one machine. Its contents are migrated once, on first read, and the setting is
/// then left empty — see <see cref="MigrateLegacyAnswersAsync"/>.
/// </para>
/// </summary>
public sealed class FormAnswerService
{
    private readonly IFormAnswerRepository _answers;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public FormAnswerService(IFormAnswerRepository answers, SettingsService settings, ActivityLogService activity)
    {
        _answers = answers;
        _settings = settings;
        _activity = activity;
    }

    public async Task<List<FormAnswer>> ListAsync(ObjectId profileId)
    {
        await MigrateLegacyAnswersAsync(profileId);
        return await _answers.ListByProfileAsync(profileId);
    }

    /// <summary>
    /// Answers usable for filling, newest wording first. Outstanding rows are excluded because an
    /// empty answer is a question for the user, not something to type into a form.
    /// </summary>
    public async Task<Dictionary<string, string>> BuildLookupAsync(ObjectId profileId, bool approvedOnly)
    {
        var rows = await ListAsync(profileId);
        return rows
            .Where(row => !row.IsOutstanding && (!approvedOnly || row.IsApproved))
            .GroupBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(row => row.IsApproved)
                    .ThenByDescending(row => row.UpdatedAt).First().Answer,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records what ChatGPT proposed. Never overwrites an answer the user approved.</summary>
    public Task RecordGeneratedAsync(ObjectId profileId, string question, string answer, string site, string fieldName = "") =>
        RecordAsync(profileId, question, answer, site, fieldName, FormAnswerSources.Gpt, approved: false);

    /// <summary>Records a question the run could not answer, so it surfaces in Job Operations.</summary>
    public Task RecordOutstandingAsync(ObjectId profileId, string question, string site, string fieldName = "") =>
        RecordAsync(profileId, question, "", site, fieldName, FormAnswerSources.Gpt, approved: false);

    public Task SaveUserAnswerAsync(ObjectId profileId, string question, string answer, string site = "", string fieldName = "") =>
        RecordAsync(profileId, question, answer, site, fieldName, FormAnswerSources.User, approved: true);

    private async Task RecordAsync(
        ObjectId profileId, string question, string answer, string site, string fieldName,
        string source, bool approved)
    {
        var key = FormAnswer.Normalise(question);
        if (key.Length == 0) return;
        await _answers.RecordAsync(new FormAnswer
        {
            ProfileId = profileId,
            FieldKey = key,
            FieldName = fieldName ?? "",
            Question = (question ?? "").Trim(),
            Answer = (answer ?? "").Trim(),
            Kind = KindOf(answer),
            Source = source,
            ApprovedAt = approved ? DateTime.UtcNow : null,
            LastSite = site ?? "",
            LastSeenAt = DateTime.UtcNow,
        });
    }

    public async Task ApproveAsync(FormAnswer answer)
    {
        answer.ApprovedAt = DateTime.UtcNow;
        answer.Source = FormAnswerSources.User;
        await _answers.UpsertAsync(answer);
    }

    public async Task SaveAsync(FormAnswer answer)
    {
        answer.Kind = KindOf(answer.Answer);
        if (!answer.IsOutstanding)
        {
            answer.Source = FormAnswerSources.User;
            answer.ApprovedAt ??= DateTime.UtcNow;
        }
        await _answers.UpsertAsync(answer);
    }

    public Task DeleteAsync(FormAnswer answer) => _answers.DeleteAsync(answer.Id);

    private static string KindOf(string? answer)
    {
        var text = (answer ?? "").Trim();
        if (text.Length == 0) return FormAnswerKinds.Text;
        if (text.Contains(',')) return FormAnswerKinds.Choice;
        return text.ToLowerInvariant() is "yes" or "no" or "true" or "false"
            ? FormAnswerKinds.Boolean
            : FormAnswerKinds.Text;
    }

    /// <summary>
    /// Moves the settings.json answer bank into the table, once. Those answers were typed by the
    /// user, so they arrive approved. The setting is cleared rather than read again, which is what
    /// makes this run exactly once per profile per machine.
    /// </summary>
    private async Task MigrateLegacyAnswersAsync(ObjectId profileId)
    {
        var settings = await _settings.GetAsync();
        var key = profileId.ToString();
        if (!settings.JobFormAnswers.TryGetValue(key, out var legacy) || legacy.Count == 0) return;

        foreach (var pair in legacy)
            await SaveUserAnswerAsync(profileId, pair.Key, pair.Value);

        var editable = await _settings.GetForEditAsync();
        editable.JobFormAnswers.Remove(key);
        await _settings.SaveAsync(editable);
        _activity.Info("Job Operations", "Saved answers moved to the shared database",
            $"{legacy.Count} answer(s) now follow this account between machines");
    }
}
