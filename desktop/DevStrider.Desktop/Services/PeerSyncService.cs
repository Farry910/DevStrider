using System.Text.Json;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;
using Npgsql;
using NpgsqlTypes;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Two-way delta sync between the local MongoDB and the shared PostgreSQL database.
///
/// <list type="bullet">
///   <item><b>Publish identity</b> — who this install is, and which profiles it owns, so
///         teammates can find you before you've pushed a single bid.</item>
///   <item><b>Push</b> — bids/interviews touched since <see cref="AppSettings.LastSyncAt"/>,
///         projected into the peer shapes (URL, resume text and comments stripped; JD kept)
///         and upserted by id.</item>
///   <item><b>Pull</b> — rows authored by someone else, into the local mirror collections.</item>
/// </list>
///
/// <para>
/// Idempotent: every write is an upsert keyed on a stable id, so running twice does what running
/// once did. <see cref="AppSettings.LastSyncAt"/> only advances on full success, and the marker is
/// taken <i>before</i> the work starts — so a row edited mid-sync is re-sent next time rather than
/// skipped. Re-sending is free; missing a row is not.
/// </para>
/// </summary>
public sealed class PeerSyncService
{
    private readonly MongoContext _local;
    private readonly SharedDbContext _shared;
    private readonly SettingsService _settings;
    private readonly ProfileService _localProfile;
    private readonly ProfilesService _profiles;
    private readonly ActivityLogService _activity;

    /// <summary>
    /// One sync at a time. The scheduler fires hourly and the user can click <b>Sync now</b> on
    /// top of it; two passes sharing one <see cref="AppSettings.LastSyncAt"/> marker would let the
    /// one that finishes second advance it past rows the other hadn't pushed.
    /// </summary>
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public PeerSyncService(
        MongoContext local,
        SharedDbContext shared,
        SettingsService settings,
        ProfileService localProfile,
        ProfilesService profiles,
        ActivityLogService activity)
    {
        _local = local;
        _shared = shared;
        _settings = settings;
        _localProfile = localProfile;
        _profiles = profiles;
        _activity = activity;
    }

    /// <summary>
    /// Full push + pull. Returns a short status for the UI and never throws — failures land in
    /// the Activity feed.
    /// </summary>
    public async Task<string> SyncAsync()
    {
        if (!await _syncLock.WaitAsync(0))
        {
            const string busy = "A sync is already running — skipped.";
            _activity.Info("Peers", "Sync skipped", busy, silent: true);
            return busy;
        }
        try { return await SyncCoreAsync(); }
        finally { _syncLock.Release(); }
    }

    private async Task<string> SyncCoreAsync()
    {
        if (!await _shared.IsConfiguredAsync())
        {
            const string msg = "Shared database isn't configured — Settings → Peer database.";
            _activity.Warning("Peers", "Sync skipped", msg);
            return msg;
        }

        var settings = await _settings.GetForEditAsync();
        var userProfile = await _localProfile.GetAsync();
        var owner = (userProfile.Username ?? "").Trim();
        if (string.IsNullOrEmpty(owner))
        {
            const string msg = "Username isn't set — Settings → Identity.";
            _activity.Warning("Peers", "Sync skipped", msg);
            return msg;
        }

        var profiles = (await _profiles.ListAsync()).ToDictionary(p => p.Id);
        var lastSync = settings.LastSyncAt;
        var newSyncMark = DateTime.UtcNow;
        int pushedBids = 0, pushedIvs = 0, pulledBids = 0, pulledIvs = 0, teamSize = 0;

        try
        {
            await using var conn = await _shared.OpenAsync();

            // The generated id has to come back before anything can be pushed — it's the
            // foreign key every bid and interview row carries.
            (var ownerId, teamSize) = await PublishIdentityAsync(conn, owner, userProfile, profiles.Values);
            await PullIdentitiesAsync(conn);

            pushedBids = await PushBidsAsync(conn, ownerId, profiles, lastSync);
            pushedIvs = await PushInterviewsAsync(conn, ownerId, profiles, lastSync);
            pulledBids = await PullBidsAsync(conn, ownerId, lastSync);
            pulledIvs = await PullInterviewsAsync(conn, ownerId, lastSync);

            settings.LastSyncAt = newSyncMark;
            await _settings.SaveAsync(settings);

            var status = $"Pushed {pushedBids} bids / {pushedIvs} interviews · " +
                         $"pulled {pulledBids} bids / {pulledIvs} interviews · " +
                         $"{teamSize} team member{(teamSize == 1 ? "" : "s")}.";
            _activity.Success("Peers", "Sync complete", status);
            return status;
        }
        catch (PostgresException ex)
        {
            _activity.Error("Peers", "Sync failed", $"{ex.MessageText} (SQLSTATE {ex.SqlState})");
            return "Sync failed — see Activity for details.";
        }
        catch (Exception ex)
        {
            // Npgsql echoes the connection string in some failures; redact before it's displayed.
            _activity.Error("Peers", "Sync failed", SharedDbCredentials.Redact(ex.Message));
            return "Sync failed — see Activity for details.";
        }
    }

    // ── identity ────────────────────────────────────────────────────────────

    /// <summary>
    /// Upsert this install's identity, then report how many members the team has. Published every
    /// sync rather than delta-gated: it's one small row, and it's what makes a teammate visible
    /// before they've bid.
    /// </summary>
    private static async Task<(long ownerId, int teamSize)> PublishIdentityAsync(
        NpgsqlConnection conn, string owner, UserProfile userProfile, IEnumerable<Profile> profiles)
    {
        var list = profiles
            .Select(p => new PeerUserProfile { Slug = p.Slug(), Name = p.Name ?? "" })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long ownerId;

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO peer_users (username, email, profiles, updated_at)
            VALUES (@u, @e, @p::jsonb, @t)
            ON CONFLICT (username) DO UPDATE SET
                email      = EXCLUDED.email,
                profiles   = EXCLUDED.profiles,
                updated_at = EXCLUDED.updated_at
            RETURNING id
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", owner);
            cmd.Parameters.AddWithValue("e", (userProfile.PersonalEmail ?? "").Trim());
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(list) });
            cmd.Parameters.AddWithValue("t", SharedDbContext.Utc(DateTime.UtcNow));
            // created_at is left to its column default on insert and deliberately absent from the
            // DO UPDATE list, so it records when the identity first appeared and never moves.
            ownerId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        if (ownerId == 0) throw new InvalidOperationException("Shared database did not return an id for this identity.");

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM peer_users", conn);
        return (ownerId, Convert.ToInt32(await count.ExecuteScalarAsync() ?? 0));
    }

    /// <summary>
    /// Mirror every published identity locally. Full replace, not a delta: the set is one row per
    /// teammate, and this way a profile someone renamed or deleted stops appearing in the picker.
    /// </summary>
    private async Task PullIdentitiesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, username, email, profiles, created_at, updated_at FROM peer_users", conn);
        await using var r = await cmd.ExecuteReaderAsync();

        var seen = new List<PeerUser>();
        while (await r.ReadAsync())
        {
            var json = r.IsDBNull(3) ? "[]" : r.GetString(3);
            List<PeerUserProfile> profiles;
            try { profiles = JsonSerializer.Deserialize<List<PeerUserProfile>>(json) ?? new(); }
            catch { profiles = new(); }   // a malformed row shouldn't sink the whole sync

            seen.Add(new PeerUser
            {
                RemoteId = r.GetInt64(0),
                Username = r.GetString(1),
                Email = r.GetString(2),
                Profiles = profiles,
                CreatedAt = r.GetDateTime(4),
                UpdatedAt = r.GetDateTime(5),
            });
        }

        foreach (var u in seen)
        {
            await _local.PeerUsers.ReplaceOneAsync(
                Builders<PeerUser>.Filter.Eq(x => x.Username, u.Username),
                u,
                new ReplaceOptions { IsUpsert = true });
        }

        // Drop mirrored identities that no longer exist upstream.
        var names = seen.Select(u => u.Username).ToList();
        await _local.PeerUsers.DeleteManyAsync(Builders<PeerUser>.Filter.Nin(x => x.Username, names));
    }

    // ── bids ────────────────────────────────────────────────────────────────

    private async Task<int> PushBidsAsync(
        NpgsqlConnection conn, long ownerId, Dictionary<MongoDB.Bson.ObjectId, Profile> profiles, DateTime since)
    {
        var mine = await _local.Bids.Find(b => b.UpdatedAt > since).ToListAsync();
        var n = 0;
        foreach (var b in mine)
        {
            if (!profiles.TryGetValue(b.ProfileId, out var prof)) continue;

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO peer_bids (id, owner_user_id, owner_profile_slug, owner_profile_name,
                                       company, role, status, origin, resume_id, primary_stacks,
                                       job_description, created_at, updated_at, first_created_at, applied_at)
                VALUES (@id, @ou, @os, @on, @co, @ro, @st, @og, @ri, @ps, @jd, @ca, @ua, @fa, @aa)
                ON CONFLICT (id) DO UPDATE SET
                    owner_user_id      = EXCLUDED.owner_user_id,
                    owner_profile_slug = EXCLUDED.owner_profile_slug,
                    owner_profile_name = EXCLUDED.owner_profile_name,
                    company            = EXCLUDED.company,
                    role               = EXCLUDED.role,
                    status             = EXCLUDED.status,
                    origin             = EXCLUDED.origin,
                    resume_id          = EXCLUDED.resume_id,
                    primary_stacks     = EXCLUDED.primary_stacks,
                    job_description    = EXCLUDED.job_description,
                    updated_at         = EXCLUDED.updated_at,
                    first_created_at   = EXCLUDED.first_created_at,
                    applied_at         = EXCLUDED.applied_at
                """, conn);

            cmd.Parameters.AddWithValue("id", b.Id.ToString());
            cmd.Parameters.AddWithValue("ou", ownerId);
            cmd.Parameters.AddWithValue("os", prof.Slug());
            cmd.Parameters.AddWithValue("on", prof.Name ?? "");
            cmd.Parameters.AddWithValue("co", b.Company ?? "");
            cmd.Parameters.AddWithValue("ro", b.Role ?? "");
            cmd.Parameters.AddWithValue("st", b.Status ?? "");
            cmd.Parameters.AddWithValue("og", b.Origin ?? "");
            cmd.Parameters.AddWithValue("ri", b.ResumeId ?? "");
            cmd.Parameters.Add(new NpgsqlParameter("ps", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = (b.PrimaryStacks ?? new()).ToArray() });
            cmd.Parameters.AddWithValue("jd", b.JobDescription ?? "");
            cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(b.CreatedAt));
            cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(b.UpdatedAt));
            cmd.Parameters.AddWithValue("fa", SharedDbContext.Utc(b.FirstCreatedAt));
            cmd.Parameters.AddWithValue("aa", (object?)SharedDbContext.Utc(b.AppliedAt) ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
            n++;
        }
        return n;
    }

    private async Task<int> PullBidsAsync(NpgsqlConnection conn, long ownerId, DateTime since)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, owner_user_id, owner_profile_slug, owner_profile_name, company, role,
                   status, origin, resume_id, primary_stacks, job_description, created_at,
                   updated_at, first_created_at, applied_at
            FROM peer_bids
            WHERE updated_at > @since AND owner_user_id <> @owner
            """, conn);
        cmd.Parameters.AddWithValue("since", SharedDbContext.Utc(since));
        cmd.Parameters.AddWithValue("owner", ownerId);

        var rows = new List<PeerBid>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new PeerBid
                {
                    Id = MongoDB.Bson.ObjectId.TryParse(r.GetString(0), out var oid)
                        ? oid : MongoDB.Bson.ObjectId.GenerateNewId(),
                    OwnerUserId = r.GetInt64(1),
                    OwnerProfileSlug = r.GetString(2),
                    OwnerProfileName = r.GetString(3),
                    Company = r.GetString(4),
                    Role = r.GetString(5),
                    Status = r.GetString(6),
                    Origin = r.GetString(7),
                    ResumeId = r.GetString(8),
                    PrimaryStacks = r.IsDBNull(9) ? new() : ((string[])r.GetValue(9)).ToList(),
                    JobDescription = r.GetString(10),
                    CreatedAt = r.GetDateTime(11),
                    UpdatedAt = r.GetDateTime(12),
                    FirstCreatedAt = r.GetDateTime(13),
                    AppliedAt = r.IsDBNull(14) ? null : r.GetDateTime(14),
                });
            }
        }

        foreach (var b in rows)
        {
            await _local.PeerBids.ReplaceOneAsync(
                Builders<PeerBid>.Filter.Eq(x => x.Id, b.Id), b, new ReplaceOptions { IsUpsert = true });
        }
        return rows.Count;
    }

    // ── interviews ──────────────────────────────────────────────────────────

    private async Task<int> PushInterviewsAsync(
        NpgsqlConnection conn, long ownerId, Dictionary<MongoDB.Bson.ObjectId, Profile> profiles, DateTime since)
    {
        var mine = await _local.Interviews.Find(i => i.UpdatedAt > since).ToListAsync();
        var n = 0;
        foreach (var iv in mine)
        {
            if (!profiles.TryGetValue(iv.ProfileId, out var prof)) continue;

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO peer_interviews (id, owner_user_id, owner_profile_slug, owner_profile_name,
                                             process_id, company, role, interview_type, status, recruiter, resume_id,
                                             job_description, scheduled_date, scheduled_time,
                                             duration_minutes, created_at, updated_at)
                VALUES (@id, @ou, @os, @on, @pr, @co, @ro, @it, @st, @rc, @ri, @jd, @sd, @sti, @dm, @ca, @ua)
                ON CONFLICT (id) DO UPDATE SET
                    owner_user_id      = EXCLUDED.owner_user_id,
                    owner_profile_slug = EXCLUDED.owner_profile_slug,
                    owner_profile_name = EXCLUDED.owner_profile_name,
                    process_id         = EXCLUDED.process_id,
                    company            = EXCLUDED.company,
                    role               = EXCLUDED.role,
                    interview_type     = EXCLUDED.interview_type,
                    status             = EXCLUDED.status,
                    recruiter          = EXCLUDED.recruiter,
                    resume_id          = EXCLUDED.resume_id,
                    job_description    = EXCLUDED.job_description,
                    scheduled_date     = EXCLUDED.scheduled_date,
                    scheduled_time     = EXCLUDED.scheduled_time,
                    duration_minutes   = EXCLUDED.duration_minutes,
                    updated_at         = EXCLUDED.updated_at
                """, conn);

            cmd.Parameters.AddWithValue("id", iv.Id.ToString());
            cmd.Parameters.AddWithValue("ou", ownerId);
            cmd.Parameters.AddWithValue("os", prof.Slug());
            cmd.Parameters.AddWithValue("on", prof.Name ?? "");
            cmd.Parameters.AddWithValue("pr", iv.ProcessId == MongoDB.Bson.ObjectId.Empty ? "" : iv.ProcessId.ToString());
            cmd.Parameters.AddWithValue("co", iv.Company ?? "");
            cmd.Parameters.AddWithValue("ro", iv.Role ?? "");
            cmd.Parameters.AddWithValue("it", iv.InterviewType ?? "");
            cmd.Parameters.AddWithValue("st", iv.Status ?? "");
            cmd.Parameters.AddWithValue("rc", iv.Recruiter ?? "");
            cmd.Parameters.AddWithValue("ri", iv.ResumeId ?? "");
            cmd.Parameters.AddWithValue("jd", iv.AttachedJobDescription ?? "");
            cmd.Parameters.AddWithValue("sd", (object?)SharedDbContext.Utc(iv.ScheduledDate) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sti", iv.ScheduledTime ?? "");
            cmd.Parameters.AddWithValue("dm", (object?)iv.DurationMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(iv.CreatedAt));
            cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(iv.UpdatedAt));

            await cmd.ExecuteNonQueryAsync();
            n++;
        }
        return n;
    }

    private async Task<int> PullInterviewsAsync(NpgsqlConnection conn, long ownerId, DateTime since)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, owner_user_id, owner_profile_slug, owner_profile_name, process_id,
                   company, role, interview_type, status, recruiter, resume_id, job_description,
                   scheduled_date, scheduled_time, duration_minutes, created_at, updated_at
            FROM peer_interviews
            WHERE updated_at > @since AND owner_user_id <> @owner
            """, conn);
        cmd.Parameters.AddWithValue("since", SharedDbContext.Utc(since));
        cmd.Parameters.AddWithValue("owner", ownerId);

        var rows = new List<PeerInterview>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new PeerInterview
                {
                    Id = MongoDB.Bson.ObjectId.TryParse(r.GetString(0), out var oid)
                        ? oid : MongoDB.Bson.ObjectId.GenerateNewId(),
                    OwnerUserId = r.GetInt64(1),
                    OwnerProfileSlug = r.GetString(2),
                    OwnerProfileName = r.GetString(3),
                    ProcessId = r.GetString(4),
                    Company = r.GetString(5),
                    Role = r.GetString(6),
                    InterviewType = r.GetString(7),
                    Status = r.GetString(8),
                    Recruiter = r.GetString(9),
                    ResumeId = r.GetString(10),
                    JobDescription = r.GetString(11),
                    ScheduledDate = r.IsDBNull(12) ? null : r.GetDateTime(12),
                    ScheduledTime = r.GetString(13),
                    DurationMinutes = r.IsDBNull(14) ? null : r.GetInt32(14),
                    CreatedAt = r.GetDateTime(15),
                    UpdatedAt = r.GetDateTime(16),
                });
            }
        }

        foreach (var iv in rows)
        {
            await _local.PeerInterviews.ReplaceOneAsync(
                Builders<PeerInterview>.Filter.Eq(x => x.Id, iv.Id), iv, new ReplaceOptions { IsUpsert = true });
        }
        return rows.Count;
    }
}
