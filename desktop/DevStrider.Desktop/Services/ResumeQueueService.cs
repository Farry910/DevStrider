using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The resume-auto-generation queue. Owns the <c>resumeJobs</c> collection and a process-wide
/// "batch running" flag the extension respects via the polling endpoints. All jobs are scoped
/// to the active <see cref="Profile"/> — you pick a profile, paste URLs, run the batch.
///
/// <para>
/// The extension drives the actual work (scrape JD → ChatGPT → harvest); this service hands
/// out the next job (<see cref="ClaimNextAsync"/>) and records outcomes
/// (<see cref="CompleteAsync"/> / <see cref="FailAsync"/>).
/// </para>
/// </summary>
public sealed class ResumeQueueService
{
    private readonly MongoContext _db;
    private readonly ProfileContext _profileContext;

    /// <summary>True while a batch is active. The extension only gets jobs when this is on.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Fires (any thread) whenever the queue changes so the Resume tab can refresh.</summary>
    public event Action? Changed;

    public ResumeQueueService(MongoContext db, ProfileContext profileContext)
    {
        _db = db;
        _profileContext = profileContext;
    }

    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public void Start() { IsRunning = true; Changed?.Invoke(); }
    public void Stop()  { IsRunning = false; Changed?.Invoke(); }

    /// <summary>List the active profile's jobs, newest first.</summary>
    public async Task<List<ResumeJob>> ListAsync()
    {
        var pid = ActiveProfileId;
        if (pid == ObjectId.Empty) return new List<ResumeJob>();
        return await _db.ResumeJobs
            .Find(j => j.ProfileId == pid)
            .SortByDescending(j => j.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Enqueue a batch of URLs for the active profile (today's date window). Skips blanks,
    /// non-http strings, and (profile, day, url) duplicates. Returns how many were added.
    /// </summary>
    public async Task<(int added, int skipped)> EnqueueAsync(IEnumerable<string> urls)
    {
        var pid = ActiveProfileId;
        if (pid == ObjectId.Empty) return (0, 0);

        int added = 0, skipped = 0;
        var day = Today;
        foreach (var raw in urls)
        {
            var url = (raw ?? "").Trim();
            if (url.Length == 0) continue;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            var norm = UrlNorm.Normalize(url);
            var job = new ResumeJob
            {
                ProfileId = pid,
                Url = url,
                UrlNorm = norm,
                JobDate = day,
                Status = ResumeJobStatuses.Queued,
            };
            try
            {
                await _db.ResumeJobs.InsertOneAsync(job);
                added++;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                skipped++; // already queued for this profile/day
            }
        }
        if (added > 0) Changed?.Invoke();
        return (added, skipped);
    }

    /// <summary>
    /// Atomically claim the next Queued job for the active profile and flip it to Generating.
    /// Returns null when the batch is stopped or nothing's queued. Called by the polling endpoint.
    /// </summary>
    public async Task<ResumeJob?> ClaimNextAsync()
    {
        if (!IsRunning) return null;
        var pid = ActiveProfileId;
        if (pid == ObjectId.Empty) return null;

        var update = Builders<ResumeJob>.Update
            .Set(j => j.Status, ResumeJobStatuses.Generating)
            .Set(j => j.UpdatedAt, DateTime.UtcNow);
        var job = await _db.ResumeJobs.FindOneAndUpdateAsync(
            Builders<ResumeJob>.Filter.And(
                Builders<ResumeJob>.Filter.Eq(j => j.ProfileId, pid),
                Builders<ResumeJob>.Filter.Eq(j => j.Status, ResumeJobStatuses.Queued)),
            update,
            new FindOneAndUpdateOptions<ResumeJob>
            {
                Sort = Builders<ResumeJob>.Sort.Ascending(j => j.CreatedAt),
                ReturnDocument = ReturnDocument.After
            });
        if (job != null) Changed?.Invoke();
        return job;
    }

    public async Task SetStatusAsync(ObjectId jobId, string status)
    {
        await _db.ResumeJobs.UpdateOneAsync(
            j => j.Id == jobId,
            Builders<ResumeJob>.Update.Set(j => j.Status, status).Set(j => j.UpdatedAt, DateTime.UtcNow));
        Changed?.Invoke();
    }

    public async Task<ResumeJob?> GetAsync(ObjectId jobId) =>
        await _db.ResumeJobs.Find(j => j.Id == jobId).FirstOrDefaultAsync();

    /// <summary>Mark a job Done after the macro ran + the bid was recorded.</summary>
    public async Task CompleteAsync(ObjectId jobId, string jobDescription, string resumePart, string fastFeedLine, string filename1, string filename2)
    {
        await _db.ResumeJobs.UpdateOneAsync(
            j => j.Id == jobId,
            Builders<ResumeJob>.Update
                .Set(j => j.Status, ResumeJobStatuses.Done)
                .Set(j => j.JobDescription, jobDescription ?? "")
                .Set(j => j.GptResumeContent, resumePart ?? "")
                .Set(j => j.FastFeedLine, fastFeedLine ?? "")
                .Set(j => j.Filename1, filename1 ?? "")
                .Set(j => j.Filename2, filename2 ?? "")
                .Set(j => j.Error, "")
                .Set(j => j.UpdatedAt, DateTime.UtcNow));
        Changed?.Invoke();
    }

    public async Task FailAsync(ObjectId jobId, string error)
    {
        await _db.ResumeJobs.UpdateOneAsync(
            j => j.Id == jobId,
            Builders<ResumeJob>.Update
                .Set(j => j.Status, ResumeJobStatuses.Failed)
                .Set(j => j.Error, error ?? "")
                .Set(j => j.UpdatedAt, DateTime.UtcNow));
        Changed?.Invoke();
    }

    /// <summary>Re-queue every Failed job for the active profile (a retry round).</summary>
    public async Task<long> RetryFailedAsync()
    {
        var pid = ActiveProfileId;
        if (pid == ObjectId.Empty) return 0;
        var res = await _db.ResumeJobs.UpdateManyAsync(
            Builders<ResumeJob>.Filter.And(
                Builders<ResumeJob>.Filter.Eq(j => j.ProfileId, pid),
                Builders<ResumeJob>.Filter.Eq(j => j.Status, ResumeJobStatuses.Failed)),
            Builders<ResumeJob>.Update
                .Set(j => j.Status, ResumeJobStatuses.Queued)
                .Inc(j => j.RetryCount, 1)
                .Set(j => j.UpdatedAt, DateTime.UtcNow));
        if (res.ModifiedCount > 0) Changed?.Invoke();
        return res.ModifiedCount;
    }

    /// <summary>Clear finished (Done + Failed) jobs for the active profile.</summary>
    public async Task<long> ClearFinishedAsync()
    {
        var pid = ActiveProfileId;
        if (pid == ObjectId.Empty) return 0;
        var res = await _db.ResumeJobs.DeleteManyAsync(
            Builders<ResumeJob>.Filter.And(
                Builders<ResumeJob>.Filter.Eq(j => j.ProfileId, pid),
                Builders<ResumeJob>.Filter.In(j => j.Status,
                    new[] { ResumeJobStatuses.Done, ResumeJobStatuses.Failed })));
        if (res.DeletedCount > 0) Changed?.Invoke();
        return res.DeletedCount;
    }

    public Task DeleteAsync(ObjectId jobId)
    {
        var t = _db.ResumeJobs.DeleteOneAsync(j => j.Id == jobId);
        Changed?.Invoke();
        return t;
    }
}
