using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>Which browsers a proxy applies to.</summary>
public static class ProxyScopes
{
    /// <summary>Only the ChatGPT browser. The default, and the reason the setting exists.</summary>
    public const string ChatGpt = "chatgpt";

    /// <summary>Both browsers — for a network where the job boards are blocked too.</summary>
    public const string All = "all";
}

/// <summary>
/// Turns the proxy settings into the two forms anything needs them in: Chromium command-line
/// arguments for a WebView2 environment, and a <see cref="IWebProxy"/> for a plain HTTP check.
///
/// <para>
/// WebView2 takes its proxy from the arguments the environment was created with, and that
/// environment is built once per browser. There is no way to change it on a live browser, which is
/// why changing these settings asks for a restart rather than pretending to apply immediately.
/// </para>
///
/// <para>
/// The two browsers already have separate environments and separate user-data folders, which is
/// what makes "ChatGPT only" possible at all: the job sites are reachable from wherever the person
/// is — it is ChatGPT that is not — and sending page-heavy form work through an extra hop would
/// cost the slowest part of a run for nothing.
/// </para>
/// </summary>
public sealed class ProxyConfiguration(AppSettings? settings)
{
    private static readonly Regex Scheme = new(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase);

    /// <summary>Schemes Chromium understands in <c>--proxy-server</c>.</summary>
    private static readonly string[] Supported = ["http", "https", "socks4", "socks5"];

    private readonly AppSettings _settings = settings ?? new AppSettings();

    public bool Enabled => _settings.ProxyEnabled && Address.Length > 0;

    /// <summary>True when the ChatGPT browser should be built behind the proxy.</summary>
    public bool AppliesToChatGpt => Enabled;

    /// <summary>True when the job-site browsers should be too.</summary>
    public bool AppliesToJobSites =>
        Enabled && string.Equals(_settings.ProxyScope, ProxyScopes.All, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The address with a scheme, or an empty string when nothing usable was configured.
    /// A bare <c>host:port</c> becomes <c>http://host:port</c>, which is what people mean by it.
    /// </summary>
    public string Address => Normalize(_settings.ProxyAddress);

    public static string Normalize(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return "";
        if (!Scheme.IsMatch(value)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        if (!Supported.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) return "";
        if (uri.Host.Length == 0) return "";
        return uri.IsDefaultPort && uri.Port <= 0
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>Why this address cannot be used, or an empty string when it can.</summary>
    public static string Reject(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return "Enter the proxy address.";
        if (Normalize(value).Length > 0) return "";
        return Scheme.IsMatch(value) && !Supported.Any(s =>
                   value.StartsWith(s + "://", StringComparison.OrdinalIgnoreCase))
            ? "Use http, https, socks4 or socks5 — a browser accepts no other proxy scheme."
            : "That is not a usable address. Expected host:port, or scheme://host:port.";
    }

    /// <summary>
    /// Chromium arguments for a WebView2 environment, or an empty string to leave it direct.
    ///
    /// <para>
    /// The portal is always bypassed. Signing in and reading bids is company traffic on a host the
    /// person can already reach, and pushing it through someone's proxy would send the session
    /// token somewhere it has no reason to go.
    /// </para>
    /// </summary>
    public string BrowserArguments(bool forChatGpt)
    {
        var applies = forChatGpt ? AppliesToChatGpt : AppliesToJobSites;
        if (!applies) return "";

        var bypass = BypassList();
        var arguments = $"--proxy-server=\"{Address}\"";
        if (bypass.Length > 0) arguments += $" --proxy-bypass-list=\"{bypass}\"";
        return arguments;
    }

    /// <summary>The bypass list actually used: whatever was configured, plus the portal host.</summary>
    public string BypassList()
    {
        var entries = (_settings.ProxyBypassList ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (Uri.TryCreate(_settings.PortalBaseUrl, UriKind.Absolute, out var portal) &&
            portal.Host.Length > 0 &&
            !entries.Contains(portal.Host, StringComparer.OrdinalIgnoreCase))
            entries.Add(portal.Host);

        return string.Join(";", entries);
    }

    /// <summary>
    /// Answers a proxy that asks for a username and password.
    ///
    /// <para>
    /// Guarded on the challenge naming a proxy. The same event fires when a <em>site</em> asks for
    /// basic authentication, and handing a job board the proxy password because it happened to put
    /// up a login box is not a mistake worth risking. When the challenge does not say proxy, the
    /// prompt is left for the person.
    /// </para>
    /// </summary>
    public static void AttachCredentials(Microsoft.Web.WebView2.Core.CoreWebView2? core,
        ProxyConfiguration proxy)
    {
        var credential = proxy.Credential;
        if (core == null || credential == null) return;
        core.BasicAuthenticationRequested += (_, e) =>
        {
            if ((e.Challenge ?? "").IndexOf("proxy", StringComparison.OrdinalIgnoreCase) < 0) return;
            e.Response.UserName = credential.UserName;
            e.Response.Password = credential.Password;
        };
    }

    /// <summary>Credentials for a proxy that asks, or null when none were configured.</summary>
    public NetworkCredential? Credential =>
        string.IsNullOrWhiteSpace(_settings.ProxyUsername)
            ? null
            : new NetworkCredential(_settings.ProxyUsername.Trim(), _settings.ProxyPassword ?? "");

    /// <summary>
    /// Checks the proxy by asking it for a small ChatGPT URL, and says what came back.
    ///
    /// <para>
    /// The point of the setting is reaching ChatGPT, so that is what gets asked for. A proxy that
    /// answers but cannot reach ChatGPT is a different problem from one that does not answer, and
    /// this is meant to tell those apart before a run does.
    /// </para>
    /// </summary>
    public async Task<string> TestAsync(CancellationToken token = default)
    {
        var rejection = Reject(_settings.ProxyAddress);
        if (rejection.Length > 0) return rejection;

        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(Address) { Credentials = Credential },
            UseProxy = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            using var response = await client.GetAsync("https://chatgpt.com/robots.txt",
                HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return "The proxy refused the credentials (407). Check the username and password.";
            return response.IsSuccessStatusCode
                ? $"Reached ChatGPT through {Address} ({(int)response.StatusCode})."
                : $"The proxy answered, but ChatGPT returned {(int)response.StatusCode} " +
                  $"{response.ReasonPhrase}. The proxy works; ChatGPT may still be blocked beyond it.";
        }
        catch (TaskCanceledException)
        {
            return $"No answer from {Address} within 20 seconds. Check the address and the port.";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach ChatGPT through {Address}: {ex.Message}";
        }
    }
}
