using System.Text.RegularExpressions;
using DevStrider.Desktop.Models;
using Npgsql;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Builds the shared PostgreSQL connection string, from either of the two ways a Postgres
/// instance gets described:
///
/// <list type="number">
///   <item><b>Service URI</b> — what hosted providers hand you:
///         <c>postgresql://user:pass@host:5432/dbname?sslmode=require</c></item>
///   <item><b>Parts</b> — host, port, database, user, password, for anything you set up yourself.</item>
/// </list>
///
/// <para>
/// <see cref="AppSettings.SharedDbMode"/> picks which set is live; the other is retained rather
/// than cleared, so flipping between them doesn't lose what you typed. Whichever is chosen ends
/// up as an <see cref="NpgsqlConnectionStringBuilder"/> — one code path from here down.
/// </para>
///
/// <para>
/// The password lives in cleartext on the settings row like every other credential in this app.
/// See <see cref="AppSettings.SharedDbPassword"/>.
/// </para>
/// </summary>
public sealed class SharedDbCredentials
{
    public const string ModeUri = "uri";
    public const string ModeParts = "parts";

    /// <summary>Default when a URI omits the port.</summary>
    private const int DefaultPort = 5432;

    /// <summary>
    /// Both spellings are in the wild — <c>postgresql://</c> is the official one, <c>postgres://</c>
    /// is what most provider dashboards actually print.
    /// </summary>
    private static readonly string[] AcceptedSchemes = { "postgresql", "postgres" };

    private readonly SettingsService _settings;

    public SharedDbCredentials(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>True once the active mode has everything it needs to attempt a connection.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var s = await _settings.GetAsync();
        if (IsUriMode(s))
            return ParseUri(s.SharedDbUri, s.SharedDbRequireSsl).builder != null;

        return !string.IsNullOrWhiteSpace(s.SharedDbHost)
            && !string.IsNullOrWhiteSpace(s.SharedDbName)
            && !string.IsNullOrWhiteSpace(s.SharedDbUser);
    }

    /// <summary>
    /// The live connection string. Never log the result — it carries the password. Use
    /// <see cref="Redact"/> on anything derived from it that reaches the UI.
    /// </summary>
    /// <exception cref="InvalidOperationException">The active mode is incomplete or malformed.</exception>
    public async Task<string> BuildConnectionStringAsync()
    {
        var s = await _settings.GetAsync();

        if (IsUriMode(s))
        {
            var (builder, error) = ParseUri(s.SharedDbUri, s.SharedDbRequireSsl);
            if (builder == null)
                throw new InvalidOperationException(error ?? "Shared database URI isn't set — fill it in on the sign-in window, or in Settings.");
            return builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(s.SharedDbHost))
            throw new InvalidOperationException("Shared database host isn't set — fill it in on the sign-in window, or in Settings.");
        if (string.IsNullOrWhiteSpace(s.SharedDbName))
            throw new InvalidOperationException("Shared database name isn't set — fill it in on the sign-in window, or in Settings.");
        if (string.IsNullOrWhiteSpace(s.SharedDbUser))
            throw new InvalidOperationException("Shared database user isn't set — fill it in on the sign-in window, or in Settings.");

        return NewBuilder(
            host: s.SharedDbHost.Trim(),
            port: s.SharedDbPort > 0 ? s.SharedDbPort : DefaultPort,
            database: s.SharedDbName.Trim(),
            username: s.SharedDbUser.Trim(),
            password: s.SharedDbPassword ?? "",
            requireSsl: s.SharedDbRequireSsl).ConnectionString;
    }

    /// <summary>Validate a URI without connecting — powers the inline hint in Settings.</summary>
    public static (bool ok, string? error) ValidateUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return (false, "Enter a service URI.");
        var (builder, error) = ParseUri(uri, requireSsl: true);
        return builder != null ? (true, null) : (false, error);
    }

    private static bool IsUriMode(AppSettings s) =>
        !string.Equals(s.SharedDbMode, ModeParts, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turn a service URI into a builder. Npgsql takes key=value connection strings, not URIs, so
    /// this has to be taken apart by hand — including percent-decoding the credentials, since
    /// generated Postgres passwords routinely contain <c>@ : / ?</c> and arrive encoded.
    /// </summary>
    private static (NpgsqlConnectionStringBuilder? builder, string? error) ParseUri(string? raw, bool requireSsl)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, "Enter a service URI.");

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (null, "Not a valid URI. Expected postgresql://user:password@host:5432/database");

        if (!AcceptedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            return (null, $"Scheme must be postgresql:// or postgres:// — got '{uri.Scheme}://'.");

        if (string.IsNullOrWhiteSpace(uri.Host))
            return (null, "URI has no host.");

        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length == 0)
            return (null, "URI has no database name — expected it after the host, e.g. .../devstrider");

        var userInfo = (uri.UserInfo ?? "").Split(':', 2);
        var user = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        if (user.Length == 0)
            return (null, "URI has no username — expected postgresql://user:password@host/...");

        // An explicit sslmode in the query wins over the checkbox: if the provider spelled it out,
        // that's a stronger signal than a global default.
        var ssl = requireSsl;
        var sslParam = Regex.Match(uri.Query ?? "", @"[?&]sslmode=([^&]+)", RegexOptions.IgnoreCase);
        if (sslParam.Success)
        {
            var mode = Uri.UnescapeDataString(sslParam.Groups[1].Value).Trim().ToLowerInvariant();
            ssl = mode is not ("disable" or "allow");
        }

        return (NewBuilder(
            host: uri.Host,
            port: uri.Port > 0 ? uri.Port : DefaultPort,
            database: Uri.UnescapeDataString(database),
            username: user,
            password: pass,
            requireSsl: ssl), null);
    }

    private static NpgsqlConnectionStringBuilder NewBuilder(
        string host, int port, string database, string username, string password, bool requireSsl) => new()
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            // Require encrypts without demanding a chain we can verify — hosted Postgres commonly
            // presents a certificate that won't validate against the machine's root store.
            // Prefer keeps a plain local instance working.
            SslMode = requireSsl ? SslMode.Require : SslMode.Prefer,
            // Sync is bursty and infrequent; a small pool that lets connections go is the right
            // shape, and matters on free tiers with tight connection caps.
            MaxPoolSize = 5,
            Timeout = 15,
            CommandTimeout = 30,
            ApplicationName = "DevStrider",
        };

    /// <summary>
    /// Strip credentials from driver text before it reaches the Activity log or the UI. Npgsql
    /// echoes the connection string in some exceptions, and a service URI carries the password
    /// inline.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        var text = Regex.Replace(message,
            @"(postgres(?:ql)?://)[^:@/\s]+:[^@\s]*@", "$1***:***@", RegexOptions.IgnoreCase);
        return Regex.Replace(text,
            @"(Password\s*=\s*)[^;]*", "$1***", RegexOptions.IgnoreCase);
    }
}
