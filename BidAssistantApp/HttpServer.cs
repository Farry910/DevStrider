using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BidAssistantApp;

internal sealed record IncomingActivityRow(string Time, string Method, string? Path, int Status, long DurationMs);

internal sealed record OutboundActivityRow(string Time, string Method, string Endpoint, int Status, long DurationMs);

internal sealed record FoldedIncomingDto(
    string TimeStart,
    string TimeEnd,
    string Method,
    string Path,
    int Count,
    int LastStatus,
    long MinMs,
    long MaxMs,
    long AvgMs);

internal sealed record FoldedOutboundDto(
    string TimeStart,
    string TimeEnd,
    string Method,
    string Endpoint,
    int Count,
    int LastStatus,
    long MinMs,
    long MaxMs,
    long AvgMs);

/// <summary>Extension POST /client/devstrider-outcome — why DevStrider sync ran or skipped.</summary>
internal sealed record ClientDevStriderOutcomeRow(string Time, string Phase, string Code, string Detail, bool UseProxy);

partial class HttpServer
{
    private const string DevStriderBaseUrl = "https://devstrider.onrender.com";
    /// <summary>GET /api/health on Render backend to reduce cold-start delays (free tier spin-down).</summary>
    private static readonly HttpClient DevStriderKeepAliveHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(2),
    };
    private readonly string _host;
    private readonly int _port;
    private readonly SynchronizationContext _syncContext;
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private static readonly Dictionary<string, List<long>> _metrics = new();
    private static readonly List<IncomingActivityRow> _recentRequests = new();
    private static readonly List<OutboundActivityRow> _recentExternalCalls = new();
    private static readonly List<ClientDevStriderOutcomeRow> _clientDevStriderOutcomes = new();

    private static void RecordMetric(string operation, long durationMs)
    {
        lock (_metrics)
        {
            if (!_metrics.ContainsKey(operation))
                _metrics[operation] = new List<long>();
            
            var list = _metrics[operation];
            list.Add(durationMs);
            
            // Prevent memory leak: keep only last 1000 entries per operation
            if (list.Count > Constants.MAX_METRICS_PER_OPERATION)
                list.RemoveRange(0, list.Count - Constants.MAX_METRICS_PER_OPERATION);
        }
    }

    private static void AddIncoming(IncomingActivityRow row)
    {
        lock (_recentRequests)
        {
            _recentRequests.Insert(0, row);
            if (_recentRequests.Count > Constants.MAX_ACTIVITY_RAW_ENTRIES)
                _recentRequests.RemoveRange(Constants.MAX_ACTIVITY_RAW_ENTRIES, _recentRequests.Count - Constants.MAX_ACTIVITY_RAW_ENTRIES);
        }
    }

    private static void AddOutbound(OutboundActivityRow row)
    {
        lock (_recentExternalCalls)
        {
            _recentExternalCalls.Insert(0, row);
            if (_recentExternalCalls.Count > Constants.MAX_ACTIVITY_RAW_ENTRIES)
                _recentExternalCalls.RemoveRange(Constants.MAX_ACTIVITY_RAW_ENTRIES, _recentExternalCalls.Count - Constants.MAX_ACTIVITY_RAW_ENTRIES);
        }
    }

    private static void AddClientDevStriderOutcome(ClientDevStriderOutcomeRow row)
    {
        lock (_clientDevStriderOutcomes)
        {
            _clientDevStriderOutcomes.Insert(0, row);
            if (_clientDevStriderOutcomes.Count > Constants.MAX_CLIENT_DEVSTRIDER_OUTCOMES)
                _clientDevStriderOutcomes.RemoveRange(Constants.MAX_CLIENT_DEVSTRIDER_OUTCOMES, _clientDevStriderOutcomes.Count - Constants.MAX_CLIENT_DEVSTRIDER_OUTCOMES);
        }
    }

    private static List<FoldedIncomingDto> FoldIncoming(IReadOnlyList<IncomingActivityRow> rawNewestFirst)
    {
        if (rawNewestFirst.Count == 0)
            return new List<FoldedIncomingDto>();

        var chrono = rawNewestFirst.Reverse().ToList();
        var acc = new List<FoldedIncomingDto>();
        foreach (var e in chrono)
        {
            var p = e.Path ?? "";
            if (acc.Count == 0
                || !string.Equals(acc[^1].Method, e.Method, StringComparison.Ordinal)
                || !string.Equals(acc[^1].Path, p, StringComparison.Ordinal))
            {
                acc.Add(new FoldedIncomingDto(e.Time, e.Time, e.Method, p, 1, e.Status, e.DurationMs, e.DurationMs, e.DurationMs));
            }
            else
            {
                var cur = acc[^1];
                var count = cur.Count + 1;
                var sumMs = cur.AvgMs * cur.Count + e.DurationMs;
                acc[^1] = cur with
                {
                    TimeEnd = e.Time,
                    Count = count,
                    LastStatus = e.Status,
                    MinMs = Math.Min(cur.MinMs, e.DurationMs),
                    MaxMs = Math.Max(cur.MaxMs, e.DurationMs),
                    AvgMs = sumMs / count,
                };
            }
        }

        acc.Reverse();
        return acc;
    }

    private static List<FoldedOutboundDto> FoldOutbound(IReadOnlyList<OutboundActivityRow> rawNewestFirst)
    {
        if (rawNewestFirst.Count == 0)
            return new List<FoldedOutboundDto>();

        var chrono = rawNewestFirst.Reverse().ToList();
        var acc = new List<FoldedOutboundDto>();
        foreach (var e in chrono)
        {
            if (acc.Count == 0
                || !string.Equals(acc[^1].Method, e.Method, StringComparison.Ordinal)
                || !string.Equals(acc[^1].Endpoint, e.Endpoint, StringComparison.Ordinal))
            {
                acc.Add(new FoldedOutboundDto(e.Time, e.Time, e.Method, e.Endpoint, 1, e.Status, e.DurationMs, e.DurationMs, e.DurationMs));
            }
            else
            {
                var cur = acc[^1];
                var count = cur.Count + 1;
                var sumMs = cur.AvgMs * cur.Count + e.DurationMs;
                acc[^1] = cur with
                {
                    TimeEnd = e.Time,
                    Count = count,
                    LastStatus = e.Status,
                    MinMs = Math.Min(cur.MinMs, e.DurationMs),
                    MaxMs = Math.Max(cur.MaxMs, e.DurationMs),
                    AvgMs = sumMs / count,
                };
            }
        }

        acc.Reverse();
        return acc;
    }

    public HttpServer(string host, int port, SynchronizationContext syncContext)
    {
        _host = host;
        _port = port;
        _syncContext = syncContext;
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Parse(_host), _port);
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        // Request logging + dashboard activity capture
        _app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var sw = Stopwatch.StartNew();
            if (path != "/trigger-paste-submit" && path != "/health" && path != "/api/activity")
                Logger.Info($"{context.Request.Method} {path}");

            await next();
            sw.Stop();

            var skipIncomingDuplicate =
                path == "/api/activity" ||
                path == "/api/client-outcomes" ||
                (string.Equals(path, "/client/devstrider-outcome", StringComparison.Ordinal)
                && string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase));
            if (!skipIncomingDuplicate)
            {
                AddIncoming(new IncomingActivityRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    context.Request.Method,
                    path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds));
            }
        });

        // CORS middleware
        _app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            var allowOrigin = GetAllowOrigin(origin);
            context.Response.Headers["Access-Control-Allow-Origin"] = allowOrigin;
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";

            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }
            await next();
        });

        _app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        _app.MapGet("/", () => Results.Redirect("/dashboard"));

        _app.MapGet("/api/activity", (HttpRequest request) =>
        {
            var incomingPage = int.TryParse(request.Query["incomingPage"], out var ip) ? Math.Max(1, ip) : 1;
            var externalPage = int.TryParse(request.Query["externalPage"], out var ep) ? Math.Max(1, ep) : 1;
            var pageSize = int.TryParse(request.Query["pageSize"], out var psz)
                ? Math.Clamp(psz, 1, Constants.MAX_ACTIVITY_PAGE_SIZE)
                : 20;

            IncomingActivityRow[] incomingRaw;
            lock (_recentRequests)
                incomingRaw = _recentRequests.ToArray();
            OutboundActivityRow[] outboundRaw;
            lock (_recentExternalCalls)
                outboundRaw = _recentExternalCalls.ToArray();

            var foldedIn = FoldIncoming(incomingRaw);
            var foldedOut = FoldOutbound(outboundRaw);

            static int ClampPage(int page, int totalGroups, int size)
            {
                var tp = Math.Max(1, (int)Math.Ceiling(totalGroups / (double)Math.Max(1, size)));
                return Math.Clamp(page, 1, tp);
            }

            static object Paginate<T>(IReadOnlyList<T> folded, int page, int size, int totalRaw)
            {
                var totalGroups = folded.Count;
                var clamped = ClampPage(page, totalGroups, size);
                var skip = (clamped - 1) * size;
                var items = skip >= folded.Count
                    ? Array.Empty<T>()
                    : folded.Skip(skip).Take(size).ToArray();
                return new { items, page = clamped, pageSize = size, totalGroups, totalRaw };
            }

            return Results.Json(new
            {
                incoming = Paginate(foldedIn, incomingPage, pageSize, incomingRaw.Length),
                external = Paginate(foldedOut, externalPage, pageSize, outboundRaw.Length),
            });
        });

        _app.MapGet("/dashboard", () =>
        {
            const string html = """
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Bid Assistant Dashboard</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 20px; background: #0f1115; color: #e6e8eb; }
    h1 { margin: 0 0 16px 0; font-size: 22px; }
    .muted { color: #9aa3af; font-size: 12px; margin-bottom: 16px; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; align-items: start; }
    .card { background: #171a21; border: 1px solid #2a3140; border-radius: 8px; padding: 12px; }
    .card h2 { margin: 0 0 10px 0; font-size: 15px; }
    .meta { color: #9aa3af; font-size: 11px; margin: 0 0 8px 0; }
    .pager { display: flex; align-items: center; gap: 8px; margin-top: 10px; flex-wrap: wrap; }
    .pager button {
      background: #2a3140; color: #e6e8eb; border: 1px solid #3d4758; border-radius: 6px;
      padding: 6px 12px; font-size: 12px; cursor: pointer;
    }
    .pager button:disabled { opacity: 0.4; cursor: not-allowed; }
    .pager button:not(:disabled):hover { background: #374151; }
    table { width: 100%; border-collapse: collapse; font-size: 12px; }
    th, td { border-bottom: 1px solid #263041; padding: 6px 4px; text-align: left; vertical-align: top; }
    th { color: #aeb8c5; font-weight: 600; }
    .status-ok { color: #6ee7b7; }
    .status-bad { color: #fca5a5; }
    code { color: #93c5fd; word-break: break-all; }
    .badge {
      display: inline-block; min-width: 1.5em; text-align: center;
      background: #3730a3; color: #e0e7ff; border-radius: 999px;
      font-size: 11px; font-weight: 600; padding: 2px 8px;
    }
  </style>
</head>
<body>
  <h1>Bid Assistant Dashboard</h1>
  <div class="muted">Sequential identical calls are merged with a count badge. Latency shows min / avg / max (ms) per group. All paths are recorded (nothing hidden). 20 groups per page.</div>
  <div class="grid">
    <div class="card">
      <h2>Incoming Requests</h2>
      <div class="meta" id="reqMeta"></div>
      <table>
        <thead><tr><th>Time</th><th>#</th><th>Method</th><th>Path</th><th>Status</th><th>Latency (ms)</th></tr></thead>
        <tbody id="reqBody"><tr><td colspan="6">Loading...</td></tr></tbody>
      </table>
      <div class="pager">
        <button type="button" id="reqPrev">Prev</button>
        <span id="reqPageLabel"></span>
        <button type="button" id="reqNext">Next</button>
      </div>
    </div>
    <div class="card">
      <h2>External API Calls</h2>
      <div class="meta" id="extMeta"></div>
      <table>
        <thead><tr><th>Time</th><th>#</th><th>Method</th><th>Endpoint</th><th>Status</th><th>Latency (ms)</th></tr></thead>
        <tbody id="extBody"><tr><td colspan="6">Loading...</td></tr></tbody>
      </table>
      <div class="pager">
        <button type="button" id="extPrev">Prev</button>
        <span id="extPageLabel"></span>
        <button type="button" id="extNext">Next</button>
      </div>
    </div>
  </div>
  <div class="card" style="margin-top:16px;">
    <h2>DevStrider Outcomes (extension-reported)</h2>
    <div class="meta" id="dsMeta"></div>
    <table>
      <thead><tr><th>Time</th><th>Phase</th><th>Code</th><th>Proxy</th><th>Detail</th></tr></thead>
      <tbody id="dsBody"><tr><td colspan="5">Loading...</td></tr></tbody>
    </table>
    <div class="pager">
      <button type="button" id="dsPrev">Prev</button>
      <span id="dsPageLabel"></span>
      <button type="button" id="dsNext">Next</button>
    </div>
  </div>
  <script>
    var incomingPage = 1;
    var externalPage = 1;
    var dsPage = 1;
    var PAGE_SIZE = 20;

    function esc(v) {
      return String(v ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    }
    function statusCell(v) {
      var n = Number(v || 0);
      if (n === 0) return '<span class="status-bad">0</span>';
      var cls = n >= 200 && n < 300 ? "status-ok" : "status-bad";
      return '<span class="' + cls + '">' + esc(n) + "</span>";
    }
    function timeRange(x) {
      if (!x.timeStart) return "";
      if (x.timeStart === x.timeEnd) return esc(x.timeStart);
      return esc(x.timeStart) + " → " + esc(x.timeEnd);
    }
    function badgeCell(c) {
      var n = Number(c || 0);
      if (n <= 1) return esc(n);
      return '<span class="badge" title="Sequential identical calls">' + esc(n) + "</span>";
    }
    function latencyCell(x) {
      var min = Number(x.minMs), max = Number(x.maxMs), avg = Number(x.avgMs);
      if (min === max) return esc(avg);
      return esc(min) + " / " + esc(avg) + " / " + esc(max);
    }
    function codeCell(code) {
      var c = String(code || "");
      var cls = (c === "ok" || c === "OK") ? "status-ok" : (c === "" ? "" : "status-bad");
      return cls ? '<span class="' + cls + '">' + esc(c) + "</span>" : esc(c);
    }
    function renderIncoming(items, emptyText) {
      if (!items || items.length === 0) return '<tr><td colspan="6">' + emptyText + "</td></tr>";
      return items.map(function (x) {
        return "<tr><td>" + timeRange(x) + "</td><td>" + badgeCell(x.count) + "</td><td>" + esc(x.method) + "</td><td><code>" + esc(x.path) + "</code></td><td>" + statusCell(x.lastStatus) + "</td><td>" + latencyCell(x) + "</td></tr>";
      }).join("");
    }
    function renderExternal(items, emptyText) {
      if (!items || items.length === 0) return '<tr><td colspan="6">' + emptyText + "</td></tr>";
      return items.map(function (x) {
        return "<tr><td>" + timeRange(x) + "</td><td>" + badgeCell(x.count) + "</td><td>" + esc(x.method) + "</td><td><code>" + esc(x.endpoint) + "</code></td><td>" + statusCell(x.lastStatus) + "</td><td>" + latencyCell(x) + "</td></tr>";
      }).join("");
    }
    function renderDs(items, emptyText) {
      if (!items || items.length === 0) return '<tr><td colspan="5">' + emptyText + "</td></tr>";
      return items.map(function (x) {
        return "<tr><td>" + esc(x.time) + "</td><td>" + esc(x.phase) + "</td><td>" + codeCell(x.code) + "</td><td>" + (x.useProxy ? "yes" : "no") + "</td><td>" + esc(x.detail) + "</td></tr>";
      }).join("");
    }
    function totalPages(section) {
      var g = Number(section.totalGroups || section.totalPages || 0);
      var ps = Number(section.pageSize || PAGE_SIZE);
      return Math.max(1, Math.ceil(g / ps) || 1);
    }
    function setPagerButtons(section, page, prevId, nextId, labelId) {
      var tp = totalPages(section);
      var p = Math.min(Math.max(1, page), tp);
      document.getElementById(labelId).textContent = "Page " + p + " / " + tp;
      document.getElementById(prevId).disabled = p <= 1;
      document.getElementById(nextId).disabled = p >= tp;
    }
    async function refresh() {
      try {
        var q = "?incomingPage=" + incomingPage + "&externalPage=" + externalPage + "&pageSize=" + PAGE_SIZE;
        var r = await fetch("/api/activity" + q, { cache: "no-store" });
        var data = await r.json();
        var inc = data.incoming || {};
        var ext = data.external || {};
        if (inc.page != null) incomingPage = Number(inc.page);
        if (ext.page != null) externalPage = Number(ext.page);
        setPagerButtons(inc, incomingPage, "reqPrev", "reqNext", "reqPageLabel");
        setPagerButtons(ext, externalPage, "extPrev", "extNext", "extPageLabel");
        document.getElementById("reqBody").innerHTML = renderIncoming(inc.items, "No requests yet.");
        document.getElementById("extBody").innerHTML = renderExternal(ext.items, "No external calls yet.");
        document.getElementById("reqMeta").textContent =
          (inc.totalGroups || 0) + " groups · " + (inc.totalRaw || 0) + " raw calls (newest first)";
        document.getElementById("extMeta").textContent =
          (ext.totalGroups || 0) + " groups · " + (ext.totalRaw || 0) + " raw calls (newest first)";
      } catch (e) {
        document.getElementById("reqBody").innerHTML = '<tr><td colspan="6">Failed to load activity.</td></tr>';
        document.getElementById("extBody").innerHTML = '<tr><td colspan="6">Failed to load activity.</td></tr>';
      }
      try {
        var dsQ = "?page=" + dsPage + "&pageSize=" + PAGE_SIZE;
        var dsR = await fetch("/api/client-outcomes" + dsQ, { cache: "no-store" });
        var dsData = await dsR.json();
        if (dsData.page != null) dsPage = Number(dsData.page);
        var dsTp = Number(dsData.totalPages || 1);
        document.getElementById("dsPageLabel").textContent = "Page " + dsPage + " / " + dsTp;
        document.getElementById("dsPrev").disabled = dsPage <= 1;
        document.getElementById("dsNext").disabled = dsPage >= dsTp;
        document.getElementById("dsBody").innerHTML = renderDs(dsData.items, "No outcomes yet — extension will report here after each Word refresh.");
        document.getElementById("dsMeta").textContent = (dsData.total || 0) + " outcomes (newest first)";
      } catch (e) {
        document.getElementById("dsBody").innerHTML = '<tr><td colspan="5">Failed to load outcomes.</td></tr>';
      }
    }
    document.getElementById("reqPrev").addEventListener("click", function () {
      if (incomingPage > 1) { incomingPage--; refresh(); }
    });
    document.getElementById("reqNext").addEventListener("click", function () {
      incomingPage++;
      refresh();
    });
    document.getElementById("extPrev").addEventListener("click", function () {
      if (externalPage > 1) { externalPage--; refresh(); }
    });
    document.getElementById("extNext").addEventListener("click", function () {
      externalPage++;
      refresh();
    });
    document.getElementById("dsPrev").addEventListener("click", function () {
      if (dsPage > 1) { dsPage--; refresh(); }
    });
    document.getElementById("dsNext").addEventListener("click", function () {
      dsPage++;
      refresh();
    });
    refresh();
    setInterval(refresh, 2000);
  </script>
</body>
</html>
""";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        _app.MapGet("/metrics", () =>
        {
            lock (_metrics)
            {
                var summary = _metrics.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        count = kvp.Value.Count,
                        avgMs = kvp.Value.Count > 0 ? kvp.Value.Average() : 0,
                        maxMs = kvp.Value.Count > 0 ? kvp.Value.Max() : 0,
                        minMs = kvp.Value.Count > 0 ? kvp.Value.Min() : 0
                    }
                );
                return Results.Json(summary);
            }
        });

        _app.MapPost("/trigger-paste-submit", () =>
        {
            try
            {
                KeyboardHelper.PasteSubmit();
                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Paste operation failed", ex);
                return Results.Json(new { success = false, error = "Paste operation failed. Ensure the target window is focused." });
            }
        });

        _app.MapPost("/refresh-word", async (HttpContext context) =>
        {
            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            
            return HandleRefreshWord(context, body);
        });

        _app.MapGet("/browse-word", () =>
        {
            string? selectedPath = null;
            _syncContext.Send(_ =>
            {
                selectedPath = FileDialogHelper.ShowWordFileDialog();
            }, null);

            if (!string.IsNullOrEmpty(selectedPath))
                return Results.Json(new { success = true, path = selectedPath });
            return Results.Json(new { success = false, path = (string?)null });
        });

        _app.MapPost("/record-devstrider", async (HttpContext context) =>
        {
            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.Json(new { error = "Empty body" }, statusCode: 400);

            var auth = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                Program.ShowTrayNotification("Bid Assistant – Auth Error",
                    "Missing JWT. Log in on DevStrider in your browser, then try again.",
                    ToolTipIcon.Error);
                return Results.Json(
                    new { error = "Missing Authorization: Bearer <JWT>. Log in on DevStrider in your browser, then record again." },
                    statusCode: 401);
            }

            try
            {
                var sw = Stopwatch.StartNew();
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var url = $"{DevStriderBaseUrl.TrimEnd('/')}/api/integrations/bid-assistant/record-bid";
                var resp = await client.PostAsync(url, content);
                var respBody = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                AddOutbound(new OutboundActivityRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "POST",
                    url,
                    (int)resp.StatusCode,
                    sw.ElapsedMilliseconds));

                if (resp.IsSuccessStatusCode)
                {
                    // Try to pull a human-readable label from the response (e.g. company/role)
                    string bidLabel = "";
                    try
                    {
                        var doc = JsonSerializer.Deserialize<JsonElement>(respBody);
                        var company = doc.TryGetProperty("company", out var c) ? c.GetString() : null;
                        var role    = doc.TryGetProperty("role",    out var r) ? r.GetString() : null;
                        if (!string.IsNullOrEmpty(company) && !string.IsNullOrEmpty(role))
                            bidLabel = $"{company} – {role}";
                        else if (!string.IsNullOrEmpty(company))
                            bidLabel = company;
                    }
                    catch { /* label is optional */ }

                    Program.ShowTrayNotification(
                        "Bid Recorded ✓",
                        string.IsNullOrEmpty(bidLabel) ? "Bid recorded successfully in DevStrider." : $"{bidLabel}\nBid recorded successfully.",
                        ToolTipIcon.Info);
                }
                else
                {
                    string errDetail = "";
                    try
                    {
                        var doc = JsonSerializer.Deserialize<JsonElement>(respBody);
                        errDetail = doc.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(errDetail))
                            errDetail = doc.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    }
                    catch { /* ignore */ }

                    Program.ShowTrayNotification(
                        $"Bid Record Failed ({(int)resp.StatusCode})",
                        string.IsNullOrEmpty(errDetail) ? $"DevStrider returned {(int)resp.StatusCode}." : errDetail,
                        ToolTipIcon.Error);
                }

                return Results.Content(respBody, "application/json", statusCode: (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                Logger.Error("record-devstrider: request to DevStrider failed", ex);
                AddOutbound(new OutboundActivityRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "POST",
                    $"{DevStriderBaseUrl.TrimEnd('/')}/api/integrations/bid-assistant/record-bid",
                    0,
                    0L));
                Program.ShowTrayNotification(
                    "Bid Assistant – Network Error",
                    "Could not reach DevStrider. Check your internet connection.",
                    ToolTipIcon.Error);
                return Results.Json(
                    new { error = "Could not reach DevStrider API at https://devstrider.onrender.com." },
                    statusCode: 502);
            }
        });

        _app.MapPost("/client/devstrider-outcome", async (HttpContext context) =>
        {
            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(body);
                var phase    = doc.TryGetProperty("phase",    out var p1) ? p1.GetString() ?? "" : "";
                var code     = doc.TryGetProperty("code",     out var p2) ? p2.GetString() ?? "" : "";
                var detail   = doc.TryGetProperty("detail",   out var p3) ? p3.GetString() ?? "" : "";
                var useProxy = doc.TryGetProperty("useProxy", out var p4) && p4.ValueKind == JsonValueKind.True;

                AddClientDevStriderOutcome(new ClientDevStriderOutcomeRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    phase, code, detail, useProxy));

                return Results.Json(new { ok = true });
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "Invalid JSON" }, statusCode: 400);
            }
        });

        _app.MapGet("/api/client-outcomes", (HttpRequest request) =>
        {
            var page = int.TryParse(request.Query["page"], out var pg) ? Math.Max(1, pg) : 1;
            var pageSize = int.TryParse(request.Query["pageSize"], out var psz)
                ? Math.Clamp(psz, 1, Constants.MAX_ACTIVITY_PAGE_SIZE)
                : 20;

            ClientDevStriderOutcomeRow[] rows;
            lock (_clientDevStriderOutcomes)
                rows = _clientDevStriderOutcomes.ToArray();

            var total = rows.Length;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            var clamped = Math.Clamp(page, 1, totalPages);
            var items = rows.Skip((clamped - 1) * pageSize).Take(pageSize).ToArray();

            return Results.Json(new { items, page = clamped, pageSize, total, totalPages });
        });

        _cts = new CancellationTokenSource();
        _ = RunDevStriderKeepAliveAsync(_cts.Token);
        _ = _app.RunAsync(_cts.Token);
    }

    private static async Task RunDevStriderKeepAliveAsync(CancellationToken ct)
    {
        var url = $"{DevStriderBaseUrl.TrimEnd('/')}/api/health";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var resp = await DevStriderKeepAliveHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                await resp.Content.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                sw.Stop();
                AddOutbound(new OutboundActivityRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "GET",
                    url,
                    (int)resp.StatusCode,
                    sw.ElapsedMilliseconds));
                if (!resp.IsSuccessStatusCode)
                    Logger.Warning($"DevStrider keepalive {(int)resp.StatusCode} {url}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning($"DevStrider keepalive failed: {ex.Message}");
                AddOutbound(new OutboundActivityRow(
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "GET",
                    url,
                    0,
                    0L));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private string GetAllowOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return $"http://{_host}:{_port}";
        
        // Allow localhost and 127.0.0.1
        if (origin.StartsWith("http://localhost") || origin.StartsWith("http://127.0.0.1"))
            return origin;
        
        // Allow chrome extension (restrict via BID_ASSISTANT_EXTENSION_ID env var)
        if (origin.StartsWith("chrome-extension://"))
        {
            var allowedId = Environment.GetEnvironmentVariable("BID_ASSISTANT_EXTENSION_ID");
            if (string.IsNullOrEmpty(allowedId) || origin == $"chrome-extension://{allowedId}")
                return origin;
        }
        
        // Default to local origin (no wildcard for security)
        return $"http://{_host}:{_port}";
    }

    public void Stop()
    {
        Logger.Info("Shutting down server...");
        _cts?.Cancel();
        _app?.DisposeAsync().AsTask().Wait(Constants.SERVER_SHUTDOWN_TIMEOUT_MS);
        Logger.Info("Server stopped");
    }
}
