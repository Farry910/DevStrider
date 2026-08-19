using DevStrider.Desktop.Data;
using DevStrider.Desktop.Data.Import;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>What a machine's old local MongoDB still holds, without importing any of it.</summary>
public sealed record LegacyImportPreview(
    bool Available, int Profiles, int Postings, int Interviews, string Message);

/// <summary>What an import actually wrote.</summary>
public sealed record LegacyImportResult(
    bool Ok, int Profiles, int Postings, int Interviews, string Message);

/// <summary>
/// The one-time lift of a machine's own history out of its old local MongoDB and into the shared
/// database.
///
/// <para>
/// Only this machine's own work moves: <c>bidProfiles</c>, <c>links</c>, <c>bids</c> and
/// <c>interviews</c>. The local <c>peerBids</c> / <c>peerUsers</c> / <c>peerInterviews</c>
/// collections are deliberately skipped — they were a downloaded copy of what teammates had
/// published, they are not this person's to re-publish, and the originals are still sitting in the
/// shared database's own <c>peer_*</c> tables.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> Every row keeps the ObjectId it had in MongoDB and every write is an upsert
/// on that id, so running this twice imports the same rows twice into the same places rather than
/// duplicating them. That matters more than it sounds: the obvious failure mode for a one-shot
/// migration is a half-finished run nobody dares repeat.
/// </para>
///
/// <para>
/// Everything is stamped with the signed-in account by the repositories, so an import performed by
/// the wrong person would file this machine's history under their name. It runs from Settings,
/// after login, and never automatically.
/// </para>
/// </summary>
public sealed class LegacyImportService
{
    private readonly LegacyStore _legacy;
    private readonly IProfileRepository _profiles;
    private readonly IBidRepository _bids;
    private readonly IInterviewRepository _interviews;
    private readonly ProfileService _account;
    private readonly ActivityLogService _activity;

    public LegacyImportService(
        LegacyStore legacy,
        IProfileRepository profiles,
        IBidRepository bids,
        IInterviewRepository interviews,
        ProfileService account,
        ActivityLogService activity)
    {
        _legacy = legacy;
        _profiles = profiles;
        _bids = bids;
        _interviews = interviews;
        _account = account;
        _activity = activity;
    }

    /// <summary>Count what is there. Read-only, and safe to call when MongoDB isn't running.</summary>
    public async Task<LegacyImportPreview> PreviewAsync()
    {
        if (!_legacy.Available)
            return new LegacyImportPreview(false, 0, 0, 0,
                "No local MongoDB at the address in Settings — nothing to import, which is the "
                + "expected state on a machine that never ran an older DevStrider.");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var profiles = (int)await _legacy.BidProfiles.CountDocumentsAsync(
                FilterDefinition<LegacyProfile>.Empty, cancellationToken: cts.Token);
            var links = (int)await _legacy.Links.CountDocumentsAsync(
                FilterDefinition<LegacyLink>.Empty, cancellationToken: cts.Token);
            var interviews = (int)await _legacy.Interviews.CountDocumentsAsync(
                FilterDefinition<LegacyInterview>.Empty, cancellationToken: cts.Token);

            if (profiles + links + interviews == 0)
                return new LegacyImportPreview(false, 0, 0, 0, "The local database is reachable but empty.");

            return new LegacyImportPreview(true, profiles, links, interviews,
                $"Found {profiles} profile{S(profiles)}, {links} posting{S(links)} and "
                + $"{interviews} interview{S(interviews)} to import.");
        }
        catch (Exception ex)
        {
            return new LegacyImportPreview(false, 0, 0, 0, $"Couldn't read the local database: {ex.Message}");
        }
    }

    /// <summary>
    /// Import everything, profiles first so the bids and interviews under them have somewhere to
    /// belong. Returns what was written; throws nothing the caller has to catch.
    /// </summary>
    public async Task<LegacyImportResult> ImportAsync()
    {
        var preview = await PreviewAsync();
        if (!preview.Available) return new LegacyImportResult(false, 0, 0, 0, preview.Message);

        try
        {
            // Everything below hangs off the account row by foreign key.
            await _account.EnsureRowAsync();

            var contact = await _legacy.UserProfiles
                .Find(FilterDefinition<LegacyUserProfile>.Empty).FirstOrDefaultAsync();

            var profiles = await ImportProfilesAsync(contact);
            var postings = await ImportPostingsAsync();
            var interviews = await ImportInterviewsAsync();

            var message = $"Imported {profiles} profile{S(profiles)}, {postings} posting{S(postings)} "
                        + $"and {interviews} interview{S(interviews)}.";
            _activity.Success("Import", "Legacy import finished", message);
            return new LegacyImportResult(true, profiles, postings, interviews, message);
        }
        catch (Exception ex)
        {
            var message = SharedDbCredentials.Redact(ex.Message);
            _activity.Error("Import", "Legacy import failed", message);
            // Partial progress is kept on purpose: every write is an upsert on the original id, so
            // re-running finishes the job instead of doubling what already landed.
            return new LegacyImportResult(false, 0, 0, 0,
                $"Import failed part-way: {message} Re-running is safe — rows already imported are "
                + "matched by their original id, not duplicated.");
        }
    }

    private async Task<int> ImportProfilesAsync(LegacyUserProfile? contact)
    {
        var legacy = await _legacy.BidProfiles.Find(FilterDefinition<LegacyProfile>.Empty).ToListAsync();
        var existing = (await _profiles.ListAsync()).ToDictionary(p => p.Id);

        foreach (var p in legacy)
        {
            // Contact details were a single set shared by every profile in the old model, so each
            // imported profile gets the same copy. They are per-profile now; edit them afterwards.
            var profile = new Profile
            {
                Id = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Imported profile" : p.Name,
                WordDocPath = p.WordDocPath ?? "",
                MacroName = string.IsNullOrWhiteSpace(p.MacroName) ? WordMacroService.DefaultMacroName : p.MacroName,
                ResumePrompt = p.ResumePrompt ?? "",
                Headline = contact?.Headline ?? "",
                Location = contact?.Location ?? "",
                Phone = contact?.Phone ?? "",
                PersonalEmail = contact?.PersonalEmail ?? "",
                LinkedinUrl = contact?.LinkedinUrl ?? "",
                CreatedAt = Stamp(p.CreatedAt),
                UpdatedAt = Stamp(p.UpdatedAt),
            };

            // Don't overwrite a profile the user has already edited in 8.x — only its absence is
            // reason to write. Re-importing is meant to be safe, not destructive.
            if (existing.ContainsKey(profile.Id)) continue;
            await _profiles.UpsertAsync(profile);
        }
        return legacy.Count;
    }

    /// <summary>
    /// Links and bids were two collections joined one-to-one; they are one row now. The link is the
    /// spine — it is the posting, and it exists whether or not a bid was ever made against it — so
    /// the merged row keeps the link's id and a link with no bid lands as a draft.
    /// </summary>
    private async Task<int> ImportPostingsAsync()
    {
        var links = await _legacy.Links.Find(FilterDefinition<LegacyLink>.Empty).ToListAsync();
        var bids = await _legacy.Bids.Find(FilterDefinition<LegacyBid>.Empty).ToListAsync();
        var bidByLink = bids
            .GroupBy(b => b.GroupLinkId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.UpdatedAt).First());

        foreach (var link in links)
        {
            bidByLink.TryGetValue(link.Id, out var bid);

            var row = new UserBid
            {
                Id = link.Id,
                ProfileId = link.ProfileId != ObjectId.Empty ? link.ProfileId : bid?.ProfileId ?? ObjectId.Empty,
                Url = link.Url ?? "",
                UrlNorm = string.IsNullOrEmpty(link.UrlNorm) ? UrlNorm.Normalize(link.Url) : link.UrlNorm,
                MarkedUselessAt = Stamp(link.MarkedUselessAt),
                ResumeId = bid?.ResumeId ?? "",
                // The link's applied-* snapshot is the fallback, and the only source when no bid row
                // was ever created against it.
                Company = Pick(bid?.Company, link.AppliedCompany),
                Role = Pick(bid?.Role, link.AppliedRole),
                PrimaryStacks = (bid?.PrimaryStacks?.Count > 0 ? bid.PrimaryStacks : link.AppliedStacks) ?? new(),
                Status = string.IsNullOrWhiteSpace(bid?.Status) ? BidStatuses.Draft : bid!.Status,
                Origin = string.IsNullOrWhiteSpace(bid?.Origin) ? "Imported" : bid!.Origin,
                JobDescription = Pick(bid?.JobDescription, link.SharedJobDescription),
                GptResumeContent = bid?.GptResumeContent ?? "",
                Comment = bid?.Comment ?? "",
                // When the URL was captured, which is the link's creation — not the bid's.
                CreatedAt = Stamp(link.CreatedAt),
                UpdatedAt = Stamp(Later(link.UpdatedAt, bid?.UpdatedAt)),
                AppliedAt = Stamp(bid?.AppliedAt ?? link.AppliedAt),
            };
            await _bids.UpsertAsync(row);
        }
        return links.Count;
    }

    private async Task<int> ImportInterviewsAsync()
    {
        var legacy = await _legacy.Interviews.Find(FilterDefinition<LegacyInterview>.Empty).ToListAsync();
        foreach (var i in legacy)
        {
            await _interviews.UpsertAsync(new Interview
            {
                Id = i.Id,
                ProfileId = i.ProfileId,
                BidId = i.BidId,
                ParentInterviewId = i.ParentInterviewId,
                // Older rows predate the process grouping; give them one of their own so a pipeline
                // built from them doesn't collapse into a single process.
                ProcessId = i.ProcessId != ObjectId.Empty ? i.ProcessId : ObjectId.GenerateNewId(),
                MeetingLink = i.MeetingLink ?? "",
                Origin = i.Origin ?? "",
                InterviewType = string.IsNullOrWhiteSpace(i.InterviewType) ? InterviewTypes.Interview : i.InterviewType,
                Company = i.Company ?? "",
                Role = i.Role ?? "",
                Recruiter = i.Recruiter ?? "",
                AdditionalAttendees = i.AdditionalAttendees ?? new(),
                ResumeId = i.ResumeId ?? "",
                ScheduledDate = Stamp(i.ScheduledDate),
                ScheduledTime = i.ScheduledTime ?? "",
                DurationMinutes = i.DurationMinutes,
                Status = string.IsNullOrWhiteSpace(i.Status) ? InterviewStatuses.Scheduled : i.Status,
                UserComment = i.UserComment ?? "",
                AttachedJobDescription = i.AttachedJobDescription ?? "",
                AttachedResumeContent = i.AttachedResumeContent ?? "",
                ResumeObjectKey = i.ResumeObjectKey ?? "",
                ResumeFileName = i.ResumeFileName ?? "",
                ResumeSizeBytes = i.ResumeSizeBytes,
                ResumeUploadedAt = Stamp(i.ResumeUploadedAt),
                CreatedAt = Stamp(i.CreatedAt),
                UpdatedAt = Stamp(i.UpdatedAt),
            });
        }
        return legacy.Count;
    }


    private static string Pick(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred! : fallback ?? "";

    private static DateTime Later(DateTime a, DateTime? b) => b.HasValue && b.Value > a ? b.Value : a;

    /// <summary>
    /// Mongo hands back <c>Unspecified</c> kinds that are UTC in fact, and Npgsql refuses those
    /// against <c>timestamptz</c>. Stamping is right where converting would shift by the local
    /// offset — see <see cref="SharedDbContext.Utc(DateTime)"/>.
    /// </summary>
    private static DateTime Stamp(DateTime value) =>
        value == default ? DateTime.UtcNow : SharedDbContext.Utc(value);

    private static DateTime? Stamp(DateTime? value) =>
        value.HasValue && value.Value != default ? SharedDbContext.Utc(value.Value) : null;

    private static string S(int n) => n == 1 ? "" : "s";
}
