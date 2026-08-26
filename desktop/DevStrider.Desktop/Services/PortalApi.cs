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
    /// <summary>The company portal. Hard-wired — this app connects to exactly one.</summary>
    public const string Url = "https://triospace.org/hr";

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

    private readonly SessionContext _session;

    public PortalApi(SessionContext session)
    {
        _session = session;
    }

    private static Task<string> BaseAsync() => Task.FromResult(Url);

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
    /// Reachability probe used by Settings. <c>/api/me</c> is public and answers <c>null</c>
    /// to an anonymous caller — which is the "you found the portal" signal — and because it is
    /// JSON rather than a page it also rules out a proxy sitting in front of the server.
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            await GetAsync<JsonElement?>("/api/me", ct);
            return (true, $"{Url} answered.");
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
