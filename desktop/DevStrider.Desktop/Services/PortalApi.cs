using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using DevStrider.Desktop.Data.Http;

namespace DevStrider.Desktop.Services;

/// <summary>A response the portal refused. <see cref="Status"/> is the HTTP code; 0 means it never answered.</summary>
public sealed class PortalApiException : Exception
{
    public PortalApiException(int status, string message, Exception? inner = null) : base(message, inner) => Status = status;

    public int Status { get; }

    /// <summary>The token is gone or no longer valid — the only failure the app reacts to rather than reports.</summary>
    public bool IsUnauthorized => Status == (int)HttpStatusCode.Unauthorized;
}

/// <summary>
/// The one thing in this app that talks to the company portal, and the only way it reaches its
/// data at all.
///
/// <para>
/// DevStrider used to hold the portal's PostgreSQL credential on every machine and issue its own
/// SQL — including a hand-ported copy of the portal's scrypt to check passwords with. That put a
/// database password on every laptop, gave the schema no owner, and meant an authentication rule
/// changed in the portal was not changed here. All of it is gone: this class sends HTTP to
/// <c>/api/devstrider/*</c> with a bearer token, and the credential that reaches the database
/// exists only on the server.
/// </para>
///
/// <para>
/// The token comes from <see cref="SessionContext"/> on every call rather than being baked into a
/// header at construction — it is replaced on sign-in and again on each weekly refresh, and a
/// client holding a stale copy would start failing an hour after a refresh with no way to explain
/// why.
/// </para>
/// </summary>
public sealed class PortalApi
{
    /// <summary>
    /// One client for the process. <see cref="HttpClient"/> is built to be shared: one per call
    /// leaks a socket per request into TIME_WAIT, and one per call also throws away the connection
    /// pool that makes the second request fast.
    /// </summary>
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // Long enough to survive a DNS or TLS hiccup, short enough that a wrong host fails while
        // the user is still looking at the window.
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // A bid carries a whole job description and a generated resume. 100 seconds is the .NET
        // default and it is the wrong shape here: the request is big, not slow.
        Timeout = TimeSpan.FromSeconds(60),
    };

    private readonly SettingsService _settings;
    private readonly SessionContext _session;

    public PortalApi(SettingsService settings, SessionContext session)
    {
        _settings = settings;
        _session = session;
    }

    /// <summary>True once the settings file names something this app could try to reach.</summary>
    public async Task<bool> IsConfiguredAsync() =>
        ParseBaseUrl((await _settings.GetAsync()).PortalBaseUrl).baseUrl != null;

    /// <summary>
    /// Normalise whatever was typed into the prefix every request is built on.
    ///
    /// <para>
    /// A bare host gets <c>https://</c> — nobody types the scheme, and defaulting to plain HTTP
    /// would silently put a password on the wire. A trailing <c>/api</c> is stripped because that
    /// is what people paste when they have seen an endpoint rather than the site, and the paths
    /// below already start with <c>/api</c>.
    /// </para>
    ///
    /// <para>
    /// A <b>string</b> and not a <see cref="Uri"/>, and never with a trailing slash. That is not
    /// stylistic: <c>new Uri("https://host").ToString()</c> hands back <c>https://host/</c>, so
    /// concatenating a path that starts with <c>/</c> produced <c>https://host//api/me</c> — which
    /// the portal's router does not match, because it is a different number of path segments. It
    /// fell through to the static handler, redirected, and the whole thing surfaced as a 302 on
    /// every single call. Keeping the joined form as text is what makes that unrepresentable.
    /// </para>
    /// </summary>
    public static (string? baseUrl, string? error) ParseBaseUrl(string? raw)
    {
        var text = (raw ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return (null, "Enter the address of the company portal, e.g. https://triospace.org/hr");

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (null, "That isn't a valid address. Expected something like https://triospace.org/hr");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (null, $"Address must be http:// or https:// — got '{uri.Scheme}://'.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return ($"{uri.Scheme}://{uri.Authority}{path}", null);
    }

    private async Task<string> BaseAsync()
    {
        var (baseUrl, error) = ParseBaseUrl((await _settings.GetAsync()).PortalBaseUrl);
        if (baseUrl == null) throw new PortalApiException(0, error ?? "The portal address isn't set.");
        return baseUrl;
    }

    // ── verbs ───────────────────────────────────────────────────────────────

    public Task<T?> GetAsync<T>(string path, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Get, path, null, ct);

    public Task<T?> PostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Post, path, body, ct);

    public Task<T?> PutAsync<T>(string path, object? body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Put, path, body, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, path, null, ct);

    /// <summary>A list endpoint, with an empty list rather than null for "nothing there".</summary>
    public async Task<List<T>> ListAsync<T>(string path, CancellationToken ct = default) =>
        await GetAsync<List<T>>(path, ct) ?? new List<T>();

    /// <summary>
    /// One request.
    ///
    /// <para>
    /// Every failure leaves here as a <see cref="PortalApiException"/> carrying something a person
    /// could act on. A raw <see cref="HttpRequestException"/> reaching a view-model reads as
    /// "An error occurred while sending the request", which names neither the machine that could
    /// not be reached nor the thing that wanted it.
    /// </para>
    /// </summary>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, new Uri(await BaseAsync() + path));

        // The token is read per call, not cached on the handler: sign-in installs one and the
        // weekly refresh replaces it, and a header captured at startup would outlive both.
        var token = _session.Token;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: PortalJson.Options);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PortalApiException(0, "The portal didn't answer in time. Check the address and that the server is up.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PortalApiException(0, $"Couldn't reach the portal: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, ct);

            if (response.StatusCode == HttpStatusCode.NoContent) return default;
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(PortalJson.Options, ct);
            }
            catch (JsonException ex)
            {
                // Almost always an HTML error page from a proxy in front of the app — which means
                // the address points at something that is not this portal.
                throw new PortalApiException((int)response.StatusCode,
                    "The portal answered with something that isn't JSON — check the address points at the portal and not at a page in front of it.", ex);
            }
        }
    }

    /// <summary>The server's own <c>{"error": …}</c> where there is one; the status line otherwise.</summary>
    private static async Task<PortalApiException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? message = null;
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(PortalJson.Options, ct);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("error", out var error))
                message = error.GetString();
        }
        catch { /* a body that isn't JSON tells us nothing the status doesn't */ }

        if (status == 401)
            message = "Your DevStrider session has expired. Sign in again.";
        else if (status == 404 && string.IsNullOrEmpty(message))
            message = "The portal has no such endpoint — this address points at a build without the DevStrider API.";

        return new PortalApiException(status, message ?? $"The portal refused the request ({status} {response.ReasonPhrase}).");
    }

    /// <summary>
    /// Reachability probe for the address panel on the sign-in window and in Settings, run before
    /// there is any token to authenticate with. <c>/api/me</c> is public and answers <c>null</c>
    /// to an anonymous caller, which is exactly the "you found the portal" signal wanted here —
    /// and, because it is JSON rather than a page, it also rules out an address that lands on
    /// something other than this server.
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        var (baseUrl, error) = ParseBaseUrl((await _settings.GetAsync()).PortalBaseUrl);
        if (baseUrl == null) return (false, error!);

        try
        {
            await GetAsync<JsonElement?>("/api/me", ct);
            return (true, $"{baseUrl} answered.");
        }
        catch (PortalApiException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>A query string from the pairs whose value isn't null. Values are escaped here, once.</summary>
    public static string Query(params (string key, string? value)[] pairs)
    {
        var parts = pairs
            .Where(pair => pair.value != null)
            .Select(pair => $"{Uri.EscapeDataString(pair.key)}={Uri.EscapeDataString(pair.value!)}")
            .ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
    }
}
