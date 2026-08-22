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
        "Job-application workspace for the team. Persistent ChatGPT and job-site browsers " +
        "generate tailored resumes, fill reviewed application values, and write directly to " +
        "the company portal's PostgreSQL database. There is no local data copy or sync.";

    public string DataLocation =>
        "PostgreSQL (shared with the company portal) · configured in Settings";
    public string ListenerHint => "http://127.0.0.1:8765 (port is configurable in Settings)";
    public string SharedClusterLocation =>
        "Sign-in reads app_user; everything DevStrider stores lives in the ds_* tables";

    public string EnvVarTip =>
        "Empty / default settings fields are seeded from these DEVSTRIDER_* environment " +
        "variables on launch — useful when bootstrapping a fresh machine. Set them once " +
        "(setx DEVSTRIDER_SHARED_DB_URI \"postgresql://…\"), restart DevStrider, then " +
        "clear the env var if you want — values are saved to settings.json after first run.";

    public ObservableCollection<EnvVarRow> EnvVars { get; } = new();

    public AboutViewModel()
    {
        Add("DEVSTRIDER_MONGO_URI",          "AppSettings.MongoUri",          "Legacy local MongoDB, read once by the one-time import and never written to. Default mongodb://127.0.0.1:27017.");
        Add("DEVSTRIDER_DATABASE_NAME",      "AppSettings.DatabaseName",      "Legacy local MongoDB database name. Default 'devstrider'.");
        Add("DEVSTRIDER_SHARED_DB_URI",      "AppSettings.SharedDbUri",       "Shared PostgreSQL service URI, e.g. postgresql://user:pass@host:5432/devstrider?sslmode=require. Seeding it selects URI mode.", isSecret: true);
        Add("DEVSTRIDER_SHARED_DB_HOST",     "AppSettings.SharedDbHost",      "Shared PostgreSQL host. Seeding it selects host/port mode.");
        Add("DEVSTRIDER_SHARED_DB_PORT",     "AppSettings.SharedDbPort",      "Shared PostgreSQL port. Default 5432.");
        Add("DEVSTRIDER_SHARED_DB_NAME",     "AppSettings.SharedDbName",      "Shared PostgreSQL database name. Default 'devstrider'.");
        Add("DEVSTRIDER_SHARED_DB_USER",     "AppSettings.SharedDbUser",      "Shared PostgreSQL user.");
        Add("DEVSTRIDER_SHARED_DB_PASSWORD", "AppSettings.SharedDbPassword",  "Shared PostgreSQL password. Stored in cleartext with the other settings.", isSecret: true);
        Add("DEVSTRIDER_R2_ACCOUNT_ID",      "AppSettings.R2AccountId",       "Cloudflare R2 account id — the hex prefix of the r2.cloudflarestorage.com endpoint.");
        Add("DEVSTRIDER_R2_BUCKET",          "AppSettings.R2Bucket",          "R2 bucket holding shared resume files.");
        Add("DEVSTRIDER_R2_ACCESS_KEY_ID",   "AppSettings.R2AccessKeyId",     "R2 API token access key id.");
        Add("DEVSTRIDER_R2_SECRET_KEY",      "AppSettings.R2SecretAccessKey", "R2 API token secret. Stored in cleartext with the other settings.", isSecret: true);
        Add("DEVSTRIDER_LISTENER_PORT",      "AppSettings.ListenerPort",      "Local HTTP listener port. Default 8765.");
        Add("DEVSTRIDER_WORD_DOC_PATH",      "Default profile's WordDocPath", "Full path to the .docm with the resume macro. Seeded into the default profile on first launch; edit per-profile under Profiles afterwards.");
        Add("DEVSTRIDER_WORD_HOTKEY",        "AppSettings.WordHotkey",        "Keyboard shortcut that triggers the macro. Default F9.");
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
