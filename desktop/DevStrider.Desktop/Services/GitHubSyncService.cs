using System.Text;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;
using Octokit;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Push exports + pull peer files to/from the configured shared GitHub repo.
/// Layout in the repo:
///   <c>YYYY-MM-DD/&lt;username&gt;.json</c>
/// All files for a day live under one folder; "import a peer for date X" = read
/// <c>{date}/&lt;peer&gt;.json</c>.
/// </summary>
public class GitHubSyncService
{
    private readonly MongoContext _db;
    private readonly SettingsService _settings;
    private readonly ExportService _export;

    public GitHubSyncService(MongoContext db, SettingsService settings, ExportService export)
    {
        _db = db;
        _settings = settings;
        _export = export;
    }

    public class RepoFileMeta
    {
        public string Path { get; set; } = "";
        public string Sha { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateOnly Day { get; set; }
    }

    private async Task<(GitHubClient client, string owner, string name, string branch)> ClientAsync()
    {
        var s = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(s.GitHubRepoUrl))
            throw new InvalidOperationException("Set GitHub repo URL in Settings first.");
        var token = SecretStore.Unprotect(s.GitHubTokenProtected);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Set GitHub personal-access token in Settings first.");

        var (owner, name) = ParseRepoUrl(s.GitHubRepoUrl);
        var client = new GitHubClient(new ProductHeaderValue("DevStrider"))
        {
            Credentials = new Credentials(token)
        };
        return (client, owner, name, string.IsNullOrEmpty(s.GitHubBranch) ? "main" : s.GitHubBranch);
    }

    private static (string owner, string name) ParseRepoUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        // accept https://github.com/owner/name OR git@github.com:owner/name OR owner/name
        if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = trimmed.IndexOf(':');
            if (colon > 0) trimmed = trimmed[(colon + 1)..];
        }
        else if (trimmed.Contains("://"))
        {
            var u = new Uri(trimmed);
            trimmed = u.AbsolutePath.Trim('/');
        }
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException($"Couldn't parse GitHub repo URL: {url}");
        return (parts[^2], parts[^1]);
    }

    /// <summary>Upload today's snapshot at <c>YYYY-MM-DD/{username}.json</c>.</summary>
    public async Task PushTodayAsync(string username)
    {
        var (client, owner, name, branch) = await ClientAsync();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var path = ExportService.RepoFilePath(day, username);
        var (json, _) = await _export.BuildAsync(username);

        // Look for an existing file at that path to get its SHA (so we can update, not create-twice).
        string? existingSha = null;
        try
        {
            var existing = await client.Repository.Content.GetAllContentsByRef(owner, name, path, branch);
            existingSha = existing.FirstOrDefault()?.Sha;
        }
        catch (NotFoundException) { /* first push */ }

        var message = $"DevStrider sync · {username} · {day:yyyy-MM-dd}";
        if (existingSha == null)
        {
            await client.Repository.Content.CreateFile(owner, name, path,
                new CreateFileRequest(message, json, branch));
        }
        else
        {
            await client.Repository.Content.UpdateFile(owner, name, path,
                new UpdateFileRequest(message, json, existingSha, branch));
        }
    }

    /// <summary>List all files at <c>YYYY-MM-DD/</c>, parsing the owner from each filename.</summary>
    public async Task<List<RepoFileMeta>> ListDayAsync(DateOnly day)
    {
        var (client, owner, name, branch) = await ClientAsync();
        var folder = $"{day:yyyy-MM-dd}";
        IReadOnlyList<RepositoryContent> entries;
        try
        {
            entries = await client.Repository.Content.GetAllContentsByRef(owner, name, folder, branch);
        }
        catch (NotFoundException)
        {
            return new List<RepoFileMeta>();
        }
        return entries
            .Where(e => e.Type == ContentType.File && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(e => new RepoFileMeta
            {
                Path = e.Path,
                Sha = e.Sha,
                Day = day,
                Owner = System.IO.Path.GetFileNameWithoutExtension(e.Name)
            })
            .ToList();
    }

    /// <summary>Days available in the repo's top-level — each is one folder.</summary>
    public async Task<List<DateOnly>> ListDaysAsync()
    {
        var (client, owner, name, branch) = await ClientAsync();
        var root = await client.Repository.Content.GetAllContentsByRef(owner, name, branch);
        var days = new List<DateOnly>();
        foreach (var entry in root)
        {
            if (entry.Type != ContentType.Dir) continue;
            if (DateOnly.TryParseExact(entry.Name, "yyyy-MM-dd", out var d))
                days.Add(d);
        }
        days.Sort((a, b) => b.CompareTo(a));
        return days;
    }

    /// <summary>Download one peer's day file and persist it as an <see cref="ImportedSnapshot"/>.</summary>
    public async Task<ImportedSnapshot?> ImportFileAsync(RepoFileMeta meta)
    {
        var existing = await _db.ImportedSnapshots.Find(s => s.SourceSha == meta.Sha).FirstOrDefaultAsync();
        if (existing != null) return existing;

        var (client, owner, name, branch) = await ClientAsync();
        var blob = await client.Repository.Content.GetAllContentsByRef(owner, name, meta.Path, branch);
        var content = blob.FirstOrDefault();
        if (content == null) return null;

        var json = content.Content;
        if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(content.EncodedContent))
        {
            var bytes = Convert.FromBase64String(content.EncodedContent);
            json = Encoding.UTF8.GetString(bytes);
        }

        var snap = new ImportedSnapshot
        {
            Owner = meta.Owner,
            DayKey = $"{meta.Day:yyyy-MM-dd}",
            ExportedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow,
            PayloadJson = json ?? "",
            SourceSha = meta.Sha
        };
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<SnapshotPayload>(json ?? "");
            if (payload != null) snap.ExportedAt = payload.ExportedAt;
        }
        catch { /* leave ExportedAt = now */ }

        await _db.ImportedSnapshots.InsertOneAsync(snap);
        return snap;
    }

    public Task<List<ImportedSnapshot>> ListLocalSnapshotsAsync() =>
        _db.ImportedSnapshots
            .Find(FilterDefinition<ImportedSnapshot>.Empty)
            .SortByDescending(s => s.ImportedAt)
            .ToListAsync();

    public Task DeleteSnapshotAsync(MongoDB.Bson.ObjectId id) =>
        _db.ImportedSnapshots.DeleteOneAsync(s => s.Id == id);
}
