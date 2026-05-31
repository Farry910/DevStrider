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
    private readonly ResumeService _resumes;

    public GitHubSyncService(MongoContext db, SettingsService settings, ExportService export, ResumeService resumes)
    {
        _db = db;
        _settings = settings;
        _export = export;
        _resumes = resumes;
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

    /// <summary>
    /// End-of-day push. Uploads:
    ///   <c>YYYY-MM-DD/{username}.json</c> — data snapshot (bids, interviews, …)
    /// plus, for every resume the user uploaded between local-midnight today and now:
    ///   <c>YYYY-MM-DD/{username}/{filename}</c> — raw resume bytes (PDF/DOCX/…).
    /// Resumes are base64-encoded by Octokit; existing files are updated, new ones created.
    /// </summary>
    public async Task PushTodayAsync(string username)
    {
        var (client, owner, name, branch) = await ClientAsync();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var jsonPath = ExportService.RepoFilePath(day, username);
        var (json, _) = await _export.BuildAsync(username);

        var message = $"DevStrider sync · {username} · {day:yyyy-MM-dd}";
        await CreateOrUpdateAsync(client, owner, name, branch, jsonPath, json, message, asBase64: false);

        var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);
        var todayResumes = await _resumes.ListInRangeAsync(dayStart, dayEnd);
        var folder = SlugForRepo(username);
        foreach (var resume in todayResumes)
        {
            if (string.IsNullOrWhiteSpace(resume.FileName)) continue;
            var resumePath = $"{day:yyyy-MM-dd}/{folder}/{resume.FileName}";
            var base64 = Convert.ToBase64String(resume.Bytes);
            await CreateOrUpdateAsync(
                client, owner, name, branch, resumePath, base64,
                $"Resume · {username} · {resume.FileName}",
                asBase64: true);
        }
    }

    /// <summary>
    /// Create-or-update a single file in the repo. <paramref name="asBase64"/> tells Octokit
    /// not to re-encode (binary payloads come pre-encoded); for text snapshots we pass false
    /// and let Octokit do the UTF-8 → base64 conversion.
    /// </summary>
    private static async Task CreateOrUpdateAsync(
        GitHubClient client, string owner, string name, string branch,
        string path, string content, string message, bool asBase64)
    {
        string? existingSha = null;
        try
        {
            var existing = await client.Repository.Content.GetAllContentsByRef(owner, name, path, branch);
            existingSha = existing.FirstOrDefault()?.Sha;
        }
        catch (NotFoundException) { /* first time */ }

        if (existingSha == null)
        {
            var req = new CreateFileRequest(message, content, branch, convertContentToBase64: !asBase64);
            await client.Repository.Content.CreateFile(owner, name, path, req);
        }
        else
        {
            var req = new UpdateFileRequest(message, content, existingSha, branch, convertContentToBase64: !asBase64);
            await client.Repository.Content.UpdateFile(owner, name, path, req);
        }
    }

    /// <summary>Mirror of <c>ExportService.RepoFilePath</c>'s slug rule — keep paths consistent.</summary>
    private static string SlugForRepo(string username)
    {
        var clean = new string((username ?? "").Trim().Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(clean) ? "me" : clean.ToLowerInvariant();
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
