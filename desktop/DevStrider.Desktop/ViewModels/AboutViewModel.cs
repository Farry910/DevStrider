using System.Collections.ObjectModel;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// One row in the About → "Environment variables" table. Secret-flagged values are masked
/// in <see cref="DisplayValue"/> so the PAT / sharing key don't appear on screen.
/// </summary>
public sealed class EnvVarRow
{
    public string Name { get; init; } = "";
    public string SeedsField { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsSecret { get; init; }
    public string CurrentValue { get; init; } = "";
    public bool IsSet => !string.IsNullOrEmpty(CurrentValue);
    public string Status => IsSet ? "set" : "not set";
    public string DisplayValue =>
        IsSet ? (IsSecret ? "•••" : CurrentValue) : "—";
}

public class AboutViewModel : ViewModelBase
{
    public string Version =>
        "v" + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public string Summary =>
        "Local-first job-bid tracker. The Chrome extension records bids to the local " +
        "HTTP listener; daily snapshots are AES-GCM-encrypted with your group passphrase " +
        "and pushed to a shared GitHub repo so peers can import each other's day.";

    public string DataLocation => "MongoDB (local) · 127.0.0.1:27017/devstrider";
    public string ListenerHint => "http://127.0.0.1:8765 (port is configurable in Settings)";

    public string EnvVarTip =>
        "Empty / default settings fields are seeded from these DEVSTRIDER_* environment " +
        "variables on launch — useful when bootstrapping a fresh machine. Set them once " +
        "(setx DEVSTRIDER_GITHUB_PAT \"ghp_…\"), restart DevStrider, then clear the env " +
        "var if you like — the PAT is DPAPI-encrypted in Mongo after first run.";

    public ObservableCollection<EnvVarRow> EnvVars { get; } = new();

    public AboutViewModel()
    {
        Add("DEVSTRIDER_MONGO_URI",        "AppSettings.MongoUri",         "MongoDB connection string. Default mongodb://127.0.0.1:27017.");
        Add("DEVSTRIDER_DATABASE_NAME",    "AppSettings.DatabaseName",     "MongoDB database name. Default 'devstrider'.");
        Add("DEVSTRIDER_USERNAME",         "UserProfile.Username",         "Your username in the team repo (filename of your daily snapshot).");
        Add("DEVSTRIDER_GITHUB_REPO_URL",  "AppSettings.GitHubRepoUrl",    "https://github.com/your-team/repo for shared daily snapshots.");
        Add("DEVSTRIDER_GITHUB_BRANCH",    "AppSettings.GitHubBranch",     "Branch to push/pull. Default 'main'.");
        Add("DEVSTRIDER_GITHUB_PAT",       "AppSettings.GitHubToken",      "Personal access token (repo scope). DPAPI-encrypted after first seed.", isSecret: true);
        Add("DEVSTRIDER_LISTENER_PORT",    "AppSettings.ListenerPort",     "Local HTTP listener port. Default 8765.");
        Add("DEVSTRIDER_WORD_DOC_PATH",    "AppSettings.WordDocPath",      "Full path to the .docm containing the resume macro.");
        Add("DEVSTRIDER_WORD_HOTKEY",      "AppSettings.WordHotkey",       "Keyboard shortcut that triggers the macro. Default F9.");
        Add("DEVSTRIDER_SHARING_KEY",      "AppSettings.SharingKey",       "Group passphrase. AES-GCM key for snapshot encryption.", isSecret: true);
    }

    private void Add(string name, string field, string desc, bool isSecret = false)
    {
        EnvVars.Add(new EnvVarRow
        {
            Name = name,
            SeedsField = field,
            Description = desc,
            IsSecret = isSecret,
            CurrentValue = SettingsBootstrap.ReadEnv(name) ?? ""
        });
    }
}
