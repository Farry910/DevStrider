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
        "HTTP listener; peers see each other's bids/interviews via the shared MongoDB " +
        "cluster configured in Settings (Sync button on the Sharing tab triggers a " +
        "two-way delta sync).";

    public string DataLocation => "MongoDB (local) · 127.0.0.1:27017/devstrider";
    public string ListenerHint => "http://127.0.0.1:8765 (port is configurable in Settings)";
    public string SharedClusterLocation =>
        "Atlas (peer sync) · host / username / password configured in Settings → Peer database";

    public string EnvVarTip =>
        "Empty / default settings fields are seeded from these DEVSTRIDER_* environment " +
        "variables on launch — useful when bootstrapping a fresh machine. Set them once " +
        "(setx DEVSTRIDER_SHARED_MONGO_URI \"mongodb+srv://…\"), restart DevStrider, then " +
        "clear the env var if you want — values are saved to your local Mongo after first run.";

    public ObservableCollection<EnvVarRow> EnvVars { get; } = new();

    public AboutViewModel()
    {
        Add("DEVSTRIDER_MONGO_URI",          "AppSettings.MongoUri",          "Local MongoDB connection string. Default mongodb://127.0.0.1:27017.");
        Add("DEVSTRIDER_DATABASE_NAME",      "AppSettings.DatabaseName",      "Local MongoDB database name. Default 'devstrider'.");
        Add("DEVSTRIDER_USERNAME",           "UserProfile.Username",          "Your username in the shared cluster. Defaults to your Windows account name.");
        Add("DEVSTRIDER_SHARED_MONGO_URI",   "(legacy → split on first launch)", "Full Atlas URI. Deprecated: on launch it is split into the three fields below and this value is cleared.", isSecret: true);
        Add("DEVSTRIDER_SHARED_MONGO_USER",  "AppSettings.SharedMongoUsername", "Shared-cluster username. Not a secret on its own.");
        Add("DEVSTRIDER_SHARED_MONGO_HOST",  "AppSettings.SharedMongoHost",     "Shared-cluster host, e.g. cluster0.abcde.mongodb.net.");
        Add("DEVSTRIDER_SHARED_MONGO_PASSWORD", "AppSettings.SharedMongoPassword", "Shared-cluster password. Stored in cleartext with the rest of the settings. Seeded only when no password is saved yet.", isSecret: true);
        Add("DEVSTRIDER_SHARED_DATABASE",    "AppSettings.SharedDatabaseName","Shared DB name. Default 'devstrider-shared'.");
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
