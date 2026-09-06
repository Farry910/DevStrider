using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services.HrApi;

/// <summary>
/// A call reached hr-system and it said no. <see cref="StatusCode"/> is the HTTP status;
/// <see cref="Message"/> is the server's own <c>error</c> field where it sent one, and is safe to
/// show as-is — hr-system's DevStrider routes already word these for a human (see server.js).
/// </summary>
public sealed class HrApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public HrApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>Thrown by an authenticated call made with no token installed — a caller that queried before sign-in.</summary>
public sealed class HrApiNotSignedInException : InvalidOperationException
{
    public HrApiNotSignedInException()
        : base("No hr-system session is signed in — a request was made before login.") { }
}

/// <summary>
/// <c>/api/devstrider/auth/login</c> and <c>/refresh</c> both return this shape: the signed bearer
/// token plus who it belongs to, flattened into one object (hr-system spreads <c>{...signed,
/// ...identity}</c> — see server.js).
/// </summary>
public sealed class HrAuthResult
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public long UserId { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary><c>GET /api/devstrider/auth/session</c> — identity only, no token.</summary>
public sealed class HrIdentity
{
    public long UserId { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>
/// The seam between DevStrider and hr-system's <c>/api/devstrider/*</c> HTTP API.
///
/// <para>
/// DevStrider used to hold the shared Postgres credential directly and speak SQL to <c>ds_*</c>
/// over Npgsql. It no longer does either: hr-system owns the account (<c>app_user</c>) and the
/// <c>ds_*</c> tables, and this is the only thing in the app that talks to it. Every repository in
/// <c>Data/Http</c> is a thin translation from its interface's method calls onto the requests
/// below.
/// </para>
///
/// <para>
/// <b>Auth is a bearer token, not a connection string.</b> <see cref="LoginAsync"/> exchanges an
/// email and password for a signed, week-long JWT (hr-system's <c>lib/jwt.js</c>); every
/// authenticated request after that carries it in <c>Authorization: Bearer</c>. The token is
/// cached on this instance and persisted to <see cref="AppSettings.HrToken"/> so the app does not
/// ask for a password on every launch — see <see cref="RestoreSessionAsync"/>.
/// </para>
///
/// <para>
/// <b>The account is the token's, never the caller's.</b> No request here ever puts a user id on
/// the wire — the server reads it off the token's signature, which is what makes it impossible for
/// a caller on this side to even accidentally ask for someone else's rows.
/// </para>
/// </summary>
public sealed class HrApiClient
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string? _token;
    private DateTime? _expiresAtUtc;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new Services.ObjectIdJsonConverter() },
    };

    public HrApiClient(SettingsService settings)
    {
        _settings = settings;
    }

    // ── auth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exchange credentials for a session. A wrong address and a wrong password come back as the
    /// same <see cref="HrApiException"/> message — see hr-system's server.js for why.
    /// </summary>
    public async Task<HrAuthResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var result = await PostPublicAsync<HrAuthResult>(
            "/api/devstrider/auth/login",
            new { email, password }, ct);
        await InstallTokenAsync(result.Token, result.ExpiresAt);
        return result;
    }

    /// <summary>
    /// Silent sign-in from the token saved on disk. Returns null — never throws for an ordinary
    /// "not signed in" — when there is no saved token, it has expired, or the server no longer
    /// honours it (account deleted, role changed onto a token that predates it, server key
    /// rotated). Any of those just means the login window opens instead.
    /// </summary>
    public async Task<HrIdentity?> RestoreSessionAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetAsync();
        var token = settings.HrToken;
        var expires = settings.HrTokenExpiresAt;
        if (string.IsNullOrWhiteSpace(token) || expires is not { } exp || exp <= DateTime.UtcNow)
            return null;

        _token = token;
        _expiresAtUtc = exp;

        HrIdentity identity;
        try
        {
            identity = await GetAsync<HrIdentity>("/api/devstrider/auth/session", ct: ct);
        }
        catch (HrApiException)
        {
            await ClearTokenAsync();
            return null;
        }
        catch (HttpRequestException)
        {
            // Server unreachable is not "logged out" — keep the cached token and let whatever
            // triggered this call surface its own network error rather than bouncing to login.
            throw;
        }

        // Inside its last day: slide the week forward now rather than waiting for a 401 mid-use.
        if (exp - DateTime.UtcNow < TimeSpan.FromDays(1))
        {
            try { await RefreshAsync(ct); }
            catch { /* best-effort — the still-valid token from disk is fine for this session */ }
        }

        return identity;
    }

    public async Task<HrAuthResult> RefreshAsync(CancellationToken ct = default)
    {
        var result = await PostAsync<HrAuthResult>("/api/devstrider/auth/refresh", null, ct);
        await InstallTokenAsync(result.Token, result.ExpiresAt);
        return result;
    }

    /// <summary>
    /// Drop the token, locally and on disk. hr-system's routes are stateless bearer tokens — there
    /// is no server-side row to revoke, so this is the whole of "signing out".
    /// </summary>
    public async Task ClearTokenAsync()
    {
        _token = null;
        _expiresAtUtc = null;
        var settings = await _settings.GetForEditAsync();
        settings.HrToken = "";
        settings.HrTokenExpiresAt = null;
        await _settings.SaveAsync(settings);
    }

    private async Task InstallTokenAsync(string token, DateTime expiresAtUtc)
    {
        _token = token;
        _expiresAtUtc = expiresAtUtc;
        var settings = await _settings.GetForEditAsync();
        settings.HrToken = token;
        settings.HrTokenExpiresAt = expiresAtUtc;
        await _settings.SaveAsync(settings);
    }

    // ── generic requests ────────────────────────────────────────────────────

    public async Task<T> GetAsync<T>(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken ct = default) =>
        await SendAsync<T>(HttpMethod.Get, path, query, body: null, ct);

    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default) =>
        await SendAsync<T>(HttpMethod.Put, path, query: null, body, ct);

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        await SendAsync<T>(HttpMethod.Post, path, query: null, body, ct);

    public async Task<T> DeleteAsync<T>(string path, CancellationToken ct = default) =>
        await SendAsync<T>(HttpMethod.Delete, path, query: null, body: null, ct);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, IReadOnlyDictionary<string, string?>? query, object? body, CancellationToken ct)
    {
        if (_token == null) throw new HrApiNotSignedInException();

        var url = await BuildUrlAsync(path, query);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null) req.Content = JsonContent.Create(body, options: Json);

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A token this process believed was good just wasn't — expired between our own check
            // and the server's, or revoked by a key rotation. Drop it so the next attempt at
            // anything re-prompts for a password instead of quietly failing again.
            await ClearTokenAsync();
        }

        if (!resp.IsSuccessStatusCode)
            throw new HrApiException(resp.StatusCode, ExtractError(text) ?? $"hr-system returned {(int)resp.StatusCode}.");

        if (resp.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
            return default!;

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
        catch (JsonException ex)
        {
            throw new HrApiException(resp.StatusCode, $"hr-system's response couldn't be read: {ex.Message}");
        }
    }

    /// <summary>Login itself — the one call made with no token yet installed.</summary>
    private async Task<T> PostPublicAsync<T>(string path, object body, CancellationToken ct)
    {
        var url = await BuildUrlAsync(path, null);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HrApiException(resp.StatusCode, ExtractError(text) ?? $"hr-system returned {(int)resp.StatusCode}.");

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new HrApiException(resp.StatusCode, "hr-system returned an empty response.");
    }

    private async Task<string> BuildUrlAsync(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var settings = await _settings.GetAsync();
        var baseUrl = (settings.HrApiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException(
                "No hr-system server address is set. Check Settings and try again.");

        var url = baseUrl + path;
        if (query is { Count: > 0 })
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in query)
            {
                if (v == null) continue;
                sb.Append(sb.Length == 0 ? '?' : '&');
                sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            }
            if (sb.Length > 0) url += sb.ToString();
        }
        return url;
    }

    /// <summary>hr-system's error responses are always <c>{ "error": "..." }</c>. Anything else means the body wasn't one.</summary>
    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
