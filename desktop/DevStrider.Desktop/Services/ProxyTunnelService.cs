using System.Net;
using System.Net.Sockets;
using System.Text;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Routes outbound TCP through a SOCKS5 or HTTP-CONNECT proxy, so the shared database sees the
/// proxy's address rather than this machine's.
///
/// <para><b>Why a forwarder and not a setting.</b> Npgsql has no proxy support — no
/// connection-string parameter, no hook. So this listens on <c>127.0.0.1</c>, and Npgsql is
/// pointed at that loopback port instead of the real host. Every accepted connection dials the
/// proxy, performs its handshake, and then pumps bytes in both directions.</para>
///
/// <para><b>TLS still works, but hostname validation cannot.</b> Postgres TLS is negotiated
/// end-to-end through this tunnel, so the traffic is encrypted the whole way. What breaks is
/// certificate <i>hostname</i> checking: Npgsql believes it dialled <c>127.0.0.1</c>, which no
/// server certificate names. That is fine under <c>SslMode=Require</c>, which Npgsql 8 honours
/// as "encrypt, don't validate the chain" — and it is what
/// <see cref="SharedDbCredentials"/> uses. Moving to <c>VerifyCA</c> or <c>VerifyFull</c> would
/// break every proxied connection, so change one and you must change the other.</para>
///
/// <para>
/// One listener per target endpoint, reused for the life of the process. Listeners bind to the
/// loopback interface only, so nothing outside this machine can reach them.
/// </para>
/// </summary>
public sealed class ProxyTunnelService : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public ProxyTunnelService(SettingsService settings, ActivityLogService activity)
    {
        _settings = settings;
        _activity = activity;
    }

    private sealed record Tunnel(TcpListener Listener, int Port);

    public async Task<bool> IsEnabledAsync()
    {
        var s = await _settings.GetAsync();
        return s.ProxyEnabled
            && !string.IsNullOrWhiteSpace(s.ProxyHost)
            && s.ProxyPort > 0;
    }

    /// <summary>
    /// Loopback endpoint that forwards to <paramref name="targetHost"/>:<paramref name="targetPort"/>
    /// through the configured proxy. Starts the listener on first use for a given target.
    /// </summary>
    public async Task<(string host, int port)> GetLoopbackEndpointAsync(string targetHost, int targetPort)
    {
        var key = $"{targetHost}:{targetPort}";
        await _gate.WaitAsync();
        try
        {
            if (_tunnels.TryGetValue(key, out var existing)) return ("127.0.0.1", existing.Port);

            // Port 0 asks the OS for a free one, so two targets can never collide and nothing
            // needs configuring.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _tunnels[key] = new Tunnel(listener, port);

            _ = Task.Run(() => AcceptLoopAsync(listener, targetHost, targetPort, _shutdown.Token));
            _activity.Info("Proxy", "Tunnel opened",
                $"127.0.0.1:{port} → {targetHost}:{targetPort} via proxy", silent: true);
            return ("127.0.0.1", port);
        }
        finally { _gate.Release(); }
    }

    private async Task AcceptLoopAsync(TcpListener listener, string targetHost, int targetPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; }

            _ = Task.Run(() => ServeAsync(client, targetHost, targetPort, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient local, string targetHost, int targetPort, CancellationToken ct)
    {
        TcpClient? remote = null;
        try
        {
            var s = await _settings.GetAsync();
            remote = new TcpClient { NoDelay = true };

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeout);

            await remote.ConnectAsync(s.ProxyHost.Trim(), s.ProxyPort, handshakeCts.Token);
            var stream = remote.GetStream();

            if (string.Equals(s.ProxyKind, ProxyTunnelKinds.Http, StringComparison.OrdinalIgnoreCase))
                await HttpConnectAsync(stream, targetHost, targetPort, s.ProxyUsername, s.ProxyPassword, handshakeCts.Token);
            else
                await Socks5ConnectAsync(stream, targetHost, targetPort, s.ProxyUsername, s.ProxyPassword, handshakeCts.Token);

            // Handshake done — from here the proxy is transparent and Postgres (including its
            // TLS negotiation) talks straight through.
            var localStream = local.GetStream();
            var up = localStream.CopyToAsync(stream, ct);
            var down = stream.CopyToAsync(localStream, ct);
            await Task.WhenAny(up, down);
        }
        catch (Exception ex)
        {
            // One failed connection, not a fatal condition: Npgsql sees a closed socket and
            // reports its own error. Logged because a proxy that rejects every CONNECT would
            // otherwise look exactly like an unreachable database.
            _activity.Warning("Proxy", "Tunnel connection failed", ex.Message);
        }
        finally
        {
            try { local.Close(); } catch { }
            try { remote?.Close(); } catch { }
        }
    }

    // ── SOCKS5 (RFC 1928, auth per RFC 1929) ────────────────────────────────

    private static async Task Socks5ConnectAsync(
        NetworkStream stream, string host, int port, string? user, string? pass, CancellationToken ct)
    {
        var hasAuth = !string.IsNullOrEmpty(user);

        // Greeting: version 5, then the methods we support. 00 = none, 02 = username/password.
        var greeting = hasAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting, ct);

        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, ct);
        if (choice[0] != 0x05) throw new IOException("Proxy did not answer as SOCKS5.");

        if (choice[1] == 0x02)
        {
            if (!hasAuth) throw new IOException("Proxy requires a username and password.");
            var u = Encoding.UTF8.GetBytes(user!);
            var p = Encoding.UTF8.GetBytes(pass ?? "");
            if (u.Length > 255 || p.Length > 255) throw new IOException("Proxy username or password is too long.");

            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 0x01;                       // sub-negotiation version, not 0x05
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)p.Length;
            p.CopyTo(auth, 3 + u.Length);
            await stream.WriteAsync(auth, ct);

            var authReply = new byte[2];
            await stream.ReadExactlyAsync(authReply, ct);
            if (authReply[1] != 0x00) throw new IOException("Proxy rejected the username or password.");
        }
        else if (choice[1] != 0x00)
        {
            throw new IOException("Proxy offered no authentication method this app supports.");
        }

        // CONNECT to the target by DOMAIN NAME (0x03), not by IP: resolving here would defeat
        // the point, since the proxy's own resolver and route are what we want.
        var hostBytes = Encoding.UTF8.GetBytes(host);
        if (hostBytes.Length > 255) throw new IOException("Target hostname is too long for SOCKS5.");

        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;  // version
        request[1] = 0x01;  // CONNECT
        request[2] = 0x00;  // reserved
        request[3] = 0x03;  // address type: domain name
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[5 + hostBytes.Length] = (byte)(port >> 8);
        request[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(request, ct);

        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct);
        if (head[1] != 0x00) throw new IOException($"Proxy refused the connection ({Socks5Error(head[1])}).");

        // The bound address must be consumed even though it is unused, or its bytes would be
        // read as the first bytes of the Postgres stream.
        int addrLen = head[3] switch
        {
            0x01 => 4,                                    // IPv4
            0x04 => 16,                                   // IPv6
            0x03 => await ReadByteAsync(stream, ct),      // domain, length-prefixed
            _ => throw new IOException("Proxy replied with an unknown address type."),
        };
        await stream.ReadExactlyAsync(new byte[addrLen + 2], ct);   // + 2 for the port
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, ct);
        return one[0];
    }

    private static string Socks5Error(byte code) => code switch
    {
        0x01 => "general failure",
        0x02 => "not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"code {code}",
    };

    // ── HTTP CONNECT ────────────────────────────────────────────────────────

    private static async Task HttpConnectAsync(
        NetworkStream stream, string host, int port, string? user, string? pass, CancellationToken ct)
    {
        var target = $"{host}:{port}";
        var sb = new StringBuilder()
            .Append($"CONNECT {target} HTTP/1.1\r\n")
            .Append($"Host: {target}\r\n");

        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass ?? ""}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("Proxy-Connection: Keep-Alive\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

        // Read only as far as the blank line. Reading further would swallow bytes belonging to
        // the tunnelled protocol.
        var response = new StringBuilder();
        var one = new byte[1];
        while (!response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(one, ct);
            response.Append((char)one[0]);
            if (response.Length > 8192) throw new IOException("Proxy sent an oversized CONNECT response.");
        }

        var statusLine = response.ToString().Split("\r\n")[0];
        if (!statusLine.Contains(" 200", StringComparison.Ordinal))
            throw new IOException($"Proxy refused CONNECT: {statusLine}");
    }

    /// <summary>
    /// Dial the proxy and complete a handshake to <paramref name="targetHost"/> without sending
    /// anything else — proves the proxy works from Settings rather than at first sync.
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(string targetHost, int targetPort)
    {
        var s = await _settings.GetAsync();
        if (!await IsEnabledAsync()) return (false, "Proxy isn't enabled, or the host and port are blank.");

        using var client = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(HandshakeTimeout);
        try
        {
            await client.ConnectAsync(s.ProxyHost.Trim(), s.ProxyPort, cts.Token);
            var stream = client.GetStream();

            if (string.Equals(s.ProxyKind, ProxyTunnelKinds.Http, StringComparison.OrdinalIgnoreCase))
                await HttpConnectAsync(stream, targetHost, targetPort, s.ProxyUsername, s.ProxyPassword, cts.Token);
            else
                await Socks5ConnectAsync(stream, targetHost, targetPort, s.ProxyUsername, s.ProxyPassword, cts.Token);

            return (true, $"Proxy reached {targetHost}:{targetPort}.");
        }
        catch (OperationCanceledException)
        {
            return (false, $"Timed out reaching the proxy at {s.ProxyHost}:{s.ProxyPort}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch { }
        foreach (var t in _tunnels.Values)
        {
            try { t.Listener.Stop(); } catch { }
        }
        _tunnels.Clear();
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
