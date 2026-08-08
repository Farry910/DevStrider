using System.Text.RegularExpressions;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Assembles the shared-cluster connection string from its parts and keeps the legacy
/// single-URI form migrating forward.
///
/// <para>
/// The cluster is a single Atlas login shared by every install, so the password is a team-wide
/// secret rather than a per-user one: any holder can read *and delete* everyone's peer data,
/// and rotating it means updating every client. It is stored in cleartext on
/// <see cref="AppSettings.SharedMongoPassword"/> — see that field's remarks.
/// </para>
///
/// <para>
/// Splitting the URI into parts still buys two things worth keeping: the password is
/// percent-encoded correctly on the way in (Atlas generates <c>@ : / %</c> freely, each of
/// which changes how a raw URI parses), and <see cref="Redact"/> keeps the credential out of
/// driver text before it reaches the Activity log.
/// </para>
/// </summary>
public sealed class SharedMongoCredentials
{
    /// <summary>
    /// Defaults for the team cluster, so a new user only fills in the password. Both stay
    /// editable in Settings for anyone pointing at a different cluster.
    /// </summary>
    public const string DefaultUsername = "Harry910";
    public const string DefaultHost = "cluster0.mp5mgpm.mongodb.net";
    public const string DefaultOptions = "retryWrites=true&w=majority&appName=Cluster0";

    /// <summary>
    /// Matches both <c>mongodb://</c> and <c>mongodb+srv://</c>, with or without credentials,
    /// so the one-time migration can take apart whatever an existing install happens to hold.
    /// </summary>
    private static readonly Regex UriPattern = new(
        @"^(?<scheme>mongodb(?:\+srv)?)://(?:(?<user>[^:@/]+)(?::(?<pass>[^@]*))?@)?(?<host>[^/?]+)(?:/(?<db>[^?]*))?(?:\?(?<opts>.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsService _settings;

    public SharedMongoCredentials(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Sync is possible only once username, host, and password are all present.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var s = await _settings.GetAsync();
        return !string.IsNullOrWhiteSpace(s.SharedMongoUsername)
            && !string.IsNullOrWhiteSpace(s.SharedMongoHost)
            && !string.IsNullOrWhiteSpace(s.SharedMongoPassword);
    }

    /// <summary>
    /// Compose the live connection URI. Pass it straight to the driver — don't log it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Any required part is missing.</exception>
    public async Task<string> BuildUriAsync()
    {
        var s = await _settings.GetAsync();
        var user = (s.SharedMongoUsername ?? "").Trim();
        var host = (s.SharedMongoHost ?? "").Trim();
        var opts = (s.SharedMongoOptions ?? "").Trim();
        var pass = s.SharedMongoPassword ?? "";

        if (user.Length == 0 || host.Length == 0)
            throw new InvalidOperationException("Shared cluster username/host aren't set — Settings → Peer database.");
        if (pass.Length == 0)
            throw new InvalidOperationException("Shared cluster password isn't set — Settings → Peer database.");

        // Percent-encode both halves of the credential: Atlas-generated passwords routinely
        // contain @ : / and %, every one of which changes how the URI parses if passed raw.
        var uri = $"mongodb+srv://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{host}/";
        return opts.Length > 0 ? $"{uri}?{opts}" : uri;
    }

    /// <summary>
    /// One-time split of a legacy full URI in <see cref="AppSettings.SharedMongoUri"/> into the
    /// separate username / host / password / options fields. No-op once that field is empty, so
    /// it's safe to call on every launch. Returns true when it actually migrated something.
    /// </summary>
    public async Task<bool> MigrateLegacyUriAsync()
    {
        var s = await _settings.GetForEditAsync();
        var legacy = (s.SharedMongoUri ?? "").Trim();
        if (legacy.Length == 0) return false;

        var m = UriPattern.Match(legacy);
        if (!m.Success)
        {
            // Unparseable — clear it rather than keep a value nothing can consume. The user
            // re-enters the parts in Settings; nothing else reads this field.
            s.SharedMongoUri = "";
            await _settings.SaveAsync(s);
            return false;
        }

        var user = m.Groups["user"].Success ? Uri.UnescapeDataString(m.Groups["user"].Value) : "";
        var pass = m.Groups["pass"].Success ? Uri.UnescapeDataString(m.Groups["pass"].Value) : "";
        var host = m.Groups["host"].Value;
        var opts = m.Groups["opts"].Success ? m.Groups["opts"].Value : "";

        if (user.Length > 0) s.SharedMongoUsername = user;
        if (host.Length > 0) s.SharedMongoHost = host;
        if (opts.Length > 0) s.SharedMongoOptions = opts;
        if (pass.Length > 0) s.SharedMongoPassword = pass;

        s.SharedMongoUri = "";
        await _settings.SaveAsync(s);
        return true;
    }

    /// <summary>
    /// Strip embedded credentials out of driver text before it reaches the Activity log or the
    /// UI. Mongo exceptions and server descriptions quote the connection string back verbatim,
    /// which would otherwise write the shared password into a log the user might screenshot.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        return Regex.Replace(
            message,
            @"(mongodb(?:\+srv)?://)[^:@/\s]+:[^@\s]*@",
            "$1***:***@",
            RegexOptions.IgnoreCase);
    }
}
