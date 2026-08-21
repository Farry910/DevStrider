using System.Windows;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Shared state for the embedded job browser. WebView code supplies extracted visible text;
/// the user then copies it into the same ChatGPT session used by Resume Studio.
/// </summary>
public sealed partial class JobBrowserViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;
    private readonly ActivityLogService _activity;
    private string _address = "https://www.linkedin.com/jobs/";
    public string Address { get => _address; set => SetProperty(ref _address, value); }

    private string _jobDescription = "";
    public string JobDescription { get => _jobDescription; set => SetProperty(ref _jobDescription, value); }

    private string _adapterName = "Default (generic)";
    public string AdapterName { get => _adapterName; set => SetProperty(ref _adapterName, value); }

    private string _formQuestionsJson = "[]";
    public string FormQuestionsJson { get => _formQuestionsJson; set => SetProperty(ref _formQuestionsJson, value); }

    private string _currentAnswersJson = "{}";
    public string CurrentAnswersJson { get => _currentAnswersJson; set => SetProperty(ref _currentAnswersJson, value); }

    private string _savedAnswersJson = "{}";
    public string SavedAnswersJson { get => _savedAnswersJson; set => SetProperty(ref _savedAnswersJson, value); }

    private string _selectedResumePath = "";
    public string SelectedResumePath { get => _selectedResumePath; set => SetProperty(ref _selectedResumePath, value); }

    public JobBrowserViewModel(SettingsService settings, ProfileContext profiles, ActivityLogService activity)
    {
        _settings = settings;
        _profiles = profiles;
        _activity = activity;
        _profiles.ProfileChanged += () => _ = LoadSavedAnswersAsync();
        _ = LoadSavedAnswersAsync();
    }

    [RelayCommand]
    private void CopyExtractedJobDescription()
    {
        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            StatusMessage = "Extract a job description first.";
            return;
        }
        Clipboard.SetText("Job description:\n\n" + JobDescription.Trim());
        StatusMessage = "Job description copied. Paste it into the active ChatGPT resume conversation.";
    }

    [RelayCommand]
    private void CopyQuestionsForChatGpt()
    {
        if (FormQuestionsJson == "[]" || string.IsNullOrWhiteSpace(FormQuestionsJson))
        {
            StatusMessage = "Extract form questions first.";
            return;
        }
        Clipboard.SetText("Answer these job-application questions. Return ONLY JSON in this exact shape: " +
                          "{\"answers\":{\"exact question text\":\"answer\"}}. Do not invent facts.\n\nQuestions:\n" +
                          FormQuestionsJson);
        StatusMessage = "Questions copied. Paste them into ChatGPT, then paste its JSON answer into Current answers.";
    }

    [RelayCommand]
    private async Task SaveAnswersAsync()
    {
        try
        {
            var answers = ParseAnswers(SavedAnswersJson);
            var profile = _profiles.Current;
            if (profile == null) { StatusMessage = "No active profile."; return; }
            var settings = await _settings.GetForEditAsync();
            settings.JobFormAnswers ??= new Dictionary<string, Dictionary<string, string>>();
            settings.JobFormAnswers[profile.Id.ToString()] = answers;
            await _settings.SaveAsync(settings);
            StatusMessage = $"Saved {answers.Count} reusable answer(s) for {profile.Name}.";
        }
        catch (JsonException ex) { StatusMessage = "Saved answers must be a JSON object: " + ex.Message; }
    }

    public Dictionary<string, string> BuildFillValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profile = _profiles.Current;
        if (profile != null)
        {
            var names = profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            values["full name"] = profile.Name;
            if (names.Length > 0) values["first name"] = names[0];
            if (names.Length > 1) values["last name"] = names[^1];
            values["email"] = profile.PersonalEmail;
            values["phone"] = profile.Phone;
            values["location"] = profile.Location;
            values["linkedin"] = profile.LinkedinUrl;
            values["headline"] = profile.Headline;
        }
        foreach (var pair in ParseAnswers(SavedAnswersJson)) values[pair.Key] = pair.Value;
        foreach (var pair in ParseAnswers(CurrentAnswersJson)) values[pair.Key] = pair.Value;
        return values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                     .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadSavedAnswersAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var settings = await _settings.GetAsync();
        var answers = settings.JobFormAnswers?.TryGetValue(profile.Id.ToString(), out var saved) == true
            ? saved : new Dictionary<string, string>();
        SavedAnswersJson = JsonSerializer.Serialize(answers, new JsonSerializerOptions { WriteIndented = true });
    }

    public void RecordFill(string host, string adapter, int filled, int skipped)
    {
        var detail = $"{adapter} on {host}: {filled} filled, {skipped} skipped";
        if (filled > 0) _activity.Success("Job Browser", "Application fields filled", detail);
        else _activity.Warning("Job Browser", "No application fields filled", detail);
    }

    public void RecordUpload(string host, string fileName) =>
        _activity.Success("Job Browser", "Resume selected for upload", $"{fileName} on {host}");

    public void RecordFailure(string title, string detail) =>
        _activity.Error("Job Browser", title, detail);

    public void RecordWarning(string title, string detail) =>
        _activity.Warning("Job Browser", title, detail);

    private static Dictionary<string, string> ParseAnswers(string raw)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = document.RootElement;
        if (root.TryGetProperty("answers", out var wrapped)) root = wrapped;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object of question/value pairs.");
        return root.EnumerateObject()
            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }
}
