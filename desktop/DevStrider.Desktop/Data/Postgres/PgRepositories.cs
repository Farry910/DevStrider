using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Bson;
using Npgsql;

namespace DevStrider.Desktop.Data.Postgres;

// Every write here is an upsert keyed on the row's own ObjectId. That is what makes the one-time
// import re-runnable, and it means a save never has to know whether the row already exists.

/// <summary>The <c>ds_users</c> row for the signed-in account.</summary>
public sealed class PgAccountRepository : PgRepository, IAccountRepository
{
    private const string Cols = "user_id, username, created_at, updated_at";

    public PgAccountRepository(SharedDbContext db, SessionContext session) : base(db, session) { }

    public Task<UserProfile?> GetAsync() =>
        FirstOrDefaultAsync(
            $"SELECT {Cols} FROM ds_users WHERE user_id = @uid",
            cmd => cmd.Parameters.AddWithValue("uid", UserId),
            Map);

    /// <summary>
    /// Rarely called. <see cref="Services.AuthService"/> writes this row at sign-in, and there is
    /// nothing left on it that the app edits afterwards — the username is the portal's address and
    /// is re-asserted from there.
    /// </summary>
    public Task UpsertAsync(UserProfile a)
    {
        a.UserId = UserId;
        a.UpdatedAt = DateTime.UtcNow;
        return ExecuteAsync("""
            INSERT INTO ds_users (user_id, username, created_at, updated_at)
            VALUES (@uid, @un, @ca, @ua)
            ON CONFLICT (user_id) DO UPDATE SET
                username   = EXCLUDED.username,
                updated_at = EXCLUDED.updated_at
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", a.UserId);
                cmd.Parameters.AddWithValue("un", a.Username ?? "");
                cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(a.CreatedAt));
                cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(a.UpdatedAt));
            });
    }

    public async Task<bool> UsernameTakenAsync(string username) =>
        await CountAsync(
            "SELECT COUNT(*) FROM ds_users WHERE username = @un AND user_id <> @uid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("un", username ?? "");
                cmd.Parameters.AddWithValue("uid", UserId);
            }) > 0;

    private static UserProfile Map(NpgsqlDataReader r) => new()
    {
        UserId = r.GetInt64(0),
        Username = Pg.Text(r, 1),
        CreatedAt = r.GetDateTime(2),
        UpdatedAt = r.GetDateTime(3),
    };
}

/// <summary>Bidding identities. One row each — the CV lives in the profile's .docm.</summary>
public sealed class PgProfileRepository : PgRepository, IProfileRepository
{
    private const string Cols =
        "id, user_id, name, word_doc_path, macro_name, resume_prompt, headline, location, " +
        "phone, personal_email, linkedin_url, highest_education, created_at, updated_at";

    public PgProfileRepository(SharedDbContext db, SessionContext session) : base(db, session) { }

    public Task<List<Profile>> ListAsync() =>
        ListAsync(
            $"SELECT {Cols} FROM ds_profiles WHERE user_id = @uid ORDER BY created_at",
            cmd => cmd.Parameters.AddWithValue("uid", UserId),
            Map);

    public Task<Profile?> GetAsync(ObjectId id) =>
        FirstOrDefaultAsync(
            $"SELECT {Cols} FROM ds_profiles WHERE id = @id AND user_id = @uid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Pg.Hex(id));
                cmd.Parameters.AddWithValue("uid", UserId);
            },
            Map);

    /// <summary>
    /// One statement, no transaction. A profile used to span four tables — itself plus education,
    /// certifications and experience — so saving one meant deleting and reinserting the CV inside
    /// a transaction. The CV now lives in the profile's .docm, so a profile is one row again.
    /// </summary>
    public Task UpsertAsync(Profile p)
    {
        p.UserId = UserId;
        p.UpdatedAt = DateTime.UtcNow;

        return ExecuteAsync("""
            INSERT INTO ds_profiles (id, user_id, name, slug, word_doc_path, macro_name,
                                     resume_prompt, headline, location, phone, personal_email,
                                     linkedin_url, highest_education, created_at, updated_at)
            VALUES (@id, @uid, @nm, @sl, @wd, @mn, @rp, @hl, @lo, @ph, @pe, @li, @he, @ca, @ua)
            ON CONFLICT (id) DO UPDATE SET
                name              = EXCLUDED.name,
                slug              = EXCLUDED.slug,
                word_doc_path     = EXCLUDED.word_doc_path,
                macro_name        = EXCLUDED.macro_name,
                resume_prompt     = EXCLUDED.resume_prompt,
                headline          = EXCLUDED.headline,
                location          = EXCLUDED.location,
                phone             = EXCLUDED.phone,
                personal_email    = EXCLUDED.personal_email,
                linkedin_url      = EXCLUDED.linkedin_url,
                highest_education = EXCLUDED.highest_education,
                updated_at        = EXCLUDED.updated_at
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Pg.Hex(p.Id));
                cmd.Parameters.AddWithValue("uid", p.UserId);
                cmd.Parameters.AddWithValue("nm", p.Name ?? "");
                // Derived, not stored on the model: the slug must never drift from the name.
                cmd.Parameters.AddWithValue("sl", p.Slug());
                cmd.Parameters.AddWithValue("wd", p.WordDocPath ?? "");
                cmd.Parameters.AddWithValue("mn", p.MacroName ?? "");
                cmd.Parameters.AddWithValue("rp", p.ResumePrompt ?? "");
                cmd.Parameters.AddWithValue("hl", p.Headline ?? "");
                cmd.Parameters.AddWithValue("lo", p.Location ?? "");
                cmd.Parameters.AddWithValue("ph", p.Phone ?? "");
                cmd.Parameters.AddWithValue("pe", p.PersonalEmail ?? "");
                cmd.Parameters.AddWithValue("li", p.LinkedinUrl ?? "");
                cmd.Parameters.AddWithValue("he", p.HighestEducation ?? "");
                cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(p.CreatedAt));
                cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(p.UpdatedAt));
            });
    }

    public Task DeleteAsync(ObjectId id) =>
        ExecuteAsync("DELETE FROM ds_profiles WHERE id = @id AND user_id = @uid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Pg.Hex(id));
                cmd.Parameters.AddWithValue("uid", UserId);
            });

    private static Profile Map(NpgsqlDataReader r) => new()
    {
        Id = Pg.OidAt(r, 0),
        UserId = r.GetInt64(1),
        Name = Pg.Text(r, 2),
        WordDocPath = Pg.Text(r, 3),
        MacroName = Pg.Text(r, 4),
        ResumePrompt = Pg.Text(r, 5),
        Headline = Pg.Text(r, 6),
        Location = Pg.Text(r, 7),
        Phone = Pg.Text(r, 8),
        PersonalEmail = Pg.Text(r, 9),
        LinkedinUrl = Pg.Text(r, 10),
        HighestEducation = Pg.Text(r, 11),
        CreatedAt = r.GetDateTime(12),
        UpdatedAt = r.GetDateTime(13),
    };
}

public sealed class PgBidRepository : PgRepository, IBidRepository
{
    internal const string Cols =
        "id, user_id, profile_id, url, url_norm, marked_useless_at, resume_id, company, role, " +
        "primary_stacks, status, origin, job_description, gpt_resume_content, comment, " +
        "created_at, updated_at, applied_at";

    public PgBidRepository(SharedDbContext db, SessionContext session) : base(db, session) { }

    public Task<List<UserBid>> ListByProfileAsync(ObjectId profileId) =>
        ListAsync($"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
                  "ORDER BY created_at DESC",
            cmd => BindProfile(cmd, profileId), Map);

    public Task<UserBid?> FindByUrlNormAsync(ObjectId profileId, string urlNorm) =>
        FirstOrDefaultAsync(
            $"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
            "AND url_norm = @un LIMIT 1",
            cmd =>
            {
                BindProfile(cmd, profileId);
                cmd.Parameters.AddWithValue("un", urlNorm ?? "");
            }, Map);

    public Task<UserBid?> GetAsync(ObjectId id) =>
        FirstOrDefaultAsync($"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("id", Pg.Hex(id));
            }, Map);

    public Task UpsertAsync(UserBid b)
    {
        b.UserId = UserId;
        return ExecuteAsync("""
            INSERT INTO ds_bids (id, user_id, profile_id, url, url_norm, marked_useless_at,
                                 resume_id, company, role, primary_stacks, status, origin,
                                 job_description, gpt_resume_content, comment,
                                 created_at, updated_at, applied_at)
            VALUES (@id, @uid, @pid, @url, @un, @mu, @ri, @co, @ro, @ps, @st, @og,
                    @jd, @gr, @cm, @ca, @ua, @aa)
            ON CONFLICT (id) DO UPDATE SET
                profile_id         = EXCLUDED.profile_id,
                url                = EXCLUDED.url,
                url_norm           = EXCLUDED.url_norm,
                marked_useless_at  = EXCLUDED.marked_useless_at,
                resume_id          = EXCLUDED.resume_id,
                company            = EXCLUDED.company,
                role               = EXCLUDED.role,
                primary_stacks     = EXCLUDED.primary_stacks,
                status             = EXCLUDED.status,
                origin             = EXCLUDED.origin,
                job_description    = EXCLUDED.job_description,
                gpt_resume_content = EXCLUDED.gpt_resume_content,
                comment            = EXCLUDED.comment,
                updated_at         = EXCLUDED.updated_at,
                applied_at         = EXCLUDED.applied_at
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Pg.Hex(b.Id));
                cmd.Parameters.AddWithValue("uid", b.UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(b.ProfileId));
                cmd.Parameters.AddWithValue("url", b.Url ?? "");
                cmd.Parameters.AddWithValue("un", b.UrlNorm ?? "");
                cmd.Parameters.AddWithValue("mu", Pg.NullableUtc(b.MarkedUselessAt));
                cmd.Parameters.AddWithValue("ri", b.ResumeId ?? "");
                cmd.Parameters.AddWithValue("co", b.Company ?? "");
                cmd.Parameters.AddWithValue("ro", b.Role ?? "");
                cmd.Parameters.Add(Pg.Array("ps", b.PrimaryStacks));
                cmd.Parameters.AddWithValue("st", b.Status ?? "");
                cmd.Parameters.AddWithValue("og", b.Origin ?? "");
                cmd.Parameters.AddWithValue("jd", b.JobDescription ?? "");
                cmd.Parameters.AddWithValue("gr", b.GptResumeContent ?? "");
                cmd.Parameters.AddWithValue("cm", b.Comment ?? "");
                cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(b.CreatedAt));
                cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(b.UpdatedAt));
                cmd.Parameters.AddWithValue("aa", Pg.NullableUtc(b.AppliedAt));
            });
    }

    public Task DeleteAsync(ObjectId id) =>
        ExecuteAsync("DELETE FROM ds_bids WHERE user_id = @uid AND id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("id", Pg.Hex(id));
            });

    public Task<long> CountByProfileAsync(ObjectId profileId) =>
        CountAsync("SELECT COUNT(*) FROM ds_bids WHERE user_id = @uid AND profile_id = @pid",
            cmd => BindProfile(cmd, profileId));

    public Task<long> CountCreatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        CountAsync("SELECT COUNT(*) FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
                   "AND created_at >= @f AND created_at < @t",
            cmd => { BindProfile(cmd, profileId); BindWindow(cmd, fromUtc, toUtc); });

    public Task<List<UserBid>> ListByProfileUpdatedSinceAsync(ObjectId profileId, DateTime sinceUtc) =>
        ListAsync($"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
                  "AND updated_at >= @s ORDER BY updated_at DESC",
            cmd =>
            {
                BindProfile(cmd, profileId);
                cmd.Parameters.AddWithValue("s", SharedDbContext.Utc(sinceUtc));
            }, Map);

    public Task<List<UserBid>> ListByProfileUpdatedBetweenAsync(
        ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        ListAsync($"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
                  "AND updated_at >= @f AND updated_at < @t ORDER BY updated_at DESC",
            cmd => { BindProfile(cmd, profileId); BindWindow(cmd, fromUtc, toUtc); }, Map);

    public Task<List<UserBid>> ListNonDraftByProfileAsync(ObjectId profileId) =>
        ListAsync($"SELECT {Cols} FROM ds_bids WHERE user_id = @uid AND profile_id = @pid " +
                  "AND status <> @draft",
            cmd =>
            {
                BindProfile(cmd, profileId);
                cmd.Parameters.AddWithValue("draft", BidStatuses.Draft);
            }, Map);

    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId) =>
        await ExecuteAsync("UPDATE ds_bids SET profile_id = @pid WHERE user_id = @uid AND profile_id = ''",
            cmd => BindProfile(cmd, profileId));

    private void BindProfile(NpgsqlCommand cmd, ObjectId profileId)
    {
        cmd.Parameters.AddWithValue("uid", UserId);
        cmd.Parameters.AddWithValue("pid", Pg.Hex(profileId));
    }

    private static void BindWindow(NpgsqlCommand cmd, DateTime fromUtc, DateTime toUtc)
    {
        cmd.Parameters.AddWithValue("f", SharedDbContext.Utc(fromUtc));
        cmd.Parameters.AddWithValue("t", SharedDbContext.Utc(toUtc));
    }

    internal static UserBid Map(NpgsqlDataReader r) => new()
    {
        Id = Pg.OidAt(r, 0),
        UserId = r.GetInt64(1),
        ProfileId = Pg.OidAt(r, 2),
        Url = Pg.Text(r, 3),
        UrlNorm = Pg.Text(r, 4),
        MarkedUselessAt = Pg.NullableDateAt(r, 5),
        ResumeId = Pg.Text(r, 6),
        Company = Pg.Text(r, 7),
        Role = Pg.Text(r, 8),
        PrimaryStacks = Pg.StringsAt(r, 9),
        Status = Pg.Text(r, 10),
        Origin = Pg.Text(r, 11),
        JobDescription = Pg.Text(r, 12),
        GptResumeContent = Pg.Text(r, 13),
        Comment = Pg.Text(r, 14),
        CreatedAt = r.GetDateTime(15),
        UpdatedAt = r.GetDateTime(16),
        AppliedAt = Pg.NullableDateAt(r, 17),
    };
}

public sealed class PgInterviewRepository : PgRepository, IInterviewRepository
{
    internal const string Cols =
        "id, user_id, profile_id, bid_id, parent_interview_id, process_id, meeting_link, origin, " +
        "interview_type, company, role, recruiter, additional_attendees, resume_id, " +
        "scheduled_date, scheduled_time, duration_minutes, status, user_comment, " +
        "attached_job_description, attached_resume_content, resume_object_key, resume_file_name, " +
        "resume_size_bytes, resume_uploaded_at, created_at, updated_at";

    public PgInterviewRepository(SharedDbContext db, SessionContext session) : base(db, session) { }

    public Task<List<Interview>> ListByProfileScheduledBetweenAsync(
        ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        ListAsync($"SELECT {Cols} FROM ds_interviews WHERE user_id = @uid AND profile_id = @pid " +
                  "AND scheduled_date >= @f AND scheduled_date < @t ORDER BY scheduled_date",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(profileId));
                cmd.Parameters.AddWithValue("f", SharedDbContext.Utc(fromUtc));
                cmd.Parameters.AddWithValue("t", SharedDbContext.Utc(toUtc));
            }, Map);

    public Task UpsertAsync(Interview iv)
    {
        iv.UserId = UserId;
        return ExecuteAsync("""
            INSERT INTO ds_interviews (id, user_id, profile_id, bid_id, parent_interview_id,
                                       process_id, meeting_link, origin, interview_type, company,
                                       role, recruiter, additional_attendees, resume_id,
                                       scheduled_date, scheduled_time, duration_minutes, status,
                                       user_comment, attached_job_description,
                                       attached_resume_content, resume_object_key,
                                       resume_file_name, resume_size_bytes, resume_uploaded_at,
                                       created_at, updated_at)
            VALUES (@id, @uid, @pid, @bd, @par, @pr, @ml, @og, @it, @co, @ro, @rc, @aa, @ri,
                    @sd, @sti, @dm, @st, @uc, @ajd, @arc, @rok, @rfn, @rsb, @rua, @ca, @ua)
            ON CONFLICT (id) DO UPDATE SET
                profile_id               = EXCLUDED.profile_id,
                bid_id                   = EXCLUDED.bid_id,
                parent_interview_id      = EXCLUDED.parent_interview_id,
                process_id               = EXCLUDED.process_id,
                meeting_link             = EXCLUDED.meeting_link,
                origin                   = EXCLUDED.origin,
                interview_type           = EXCLUDED.interview_type,
                company                  = EXCLUDED.company,
                role                     = EXCLUDED.role,
                recruiter                = EXCLUDED.recruiter,
                additional_attendees     = EXCLUDED.additional_attendees,
                resume_id                = EXCLUDED.resume_id,
                scheduled_date           = EXCLUDED.scheduled_date,
                scheduled_time           = EXCLUDED.scheduled_time,
                duration_minutes         = EXCLUDED.duration_minutes,
                status                   = EXCLUDED.status,
                user_comment             = EXCLUDED.user_comment,
                attached_job_description = EXCLUDED.attached_job_description,
                attached_resume_content  = EXCLUDED.attached_resume_content,
                resume_object_key        = EXCLUDED.resume_object_key,
                resume_file_name         = EXCLUDED.resume_file_name,
                resume_size_bytes        = EXCLUDED.resume_size_bytes,
                resume_uploaded_at       = EXCLUDED.resume_uploaded_at,
                updated_at               = EXCLUDED.updated_at
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Pg.Hex(iv.Id));
                cmd.Parameters.AddWithValue("uid", iv.UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(iv.ProfileId));
                cmd.Parameters.AddWithValue("bd", Pg.Hex(iv.BidId));
                cmd.Parameters.AddWithValue("par",
                    iv.ParentInterviewId.HasValue ? Pg.NullIfEmpty(Pg.Hex(iv.ParentInterviewId.Value)) : DBNull.Value);
                cmd.Parameters.AddWithValue("pr", Pg.Hex(iv.ProcessId));
                cmd.Parameters.AddWithValue("ml", iv.MeetingLink ?? "");
                cmd.Parameters.AddWithValue("og", iv.Origin ?? "");
                cmd.Parameters.AddWithValue("it", iv.InterviewType ?? "");
                cmd.Parameters.AddWithValue("co", iv.Company ?? "");
                cmd.Parameters.AddWithValue("ro", iv.Role ?? "");
                cmd.Parameters.AddWithValue("rc", iv.Recruiter ?? "");
                cmd.Parameters.Add(Pg.Array("aa", iv.AdditionalAttendees));
                cmd.Parameters.AddWithValue("ri", iv.ResumeId ?? "");
                cmd.Parameters.AddWithValue("sd", Pg.NullableUtc(iv.ScheduledDate));
                cmd.Parameters.AddWithValue("sti", iv.ScheduledTime ?? "");
                cmd.Parameters.AddWithValue("dm", Pg.NullableInt(iv.DurationMinutes));
                cmd.Parameters.AddWithValue("st", iv.Status ?? "");
                cmd.Parameters.AddWithValue("uc", iv.UserComment ?? "");
                cmd.Parameters.AddWithValue("ajd", iv.AttachedJobDescription ?? "");
                cmd.Parameters.AddWithValue("arc", iv.AttachedResumeContent ?? "");
                cmd.Parameters.AddWithValue("rok", iv.ResumeObjectKey ?? "");
                cmd.Parameters.AddWithValue("rfn", iv.ResumeFileName ?? "");
                cmd.Parameters.AddWithValue("rsb", iv.ResumeSizeBytes);
                cmd.Parameters.AddWithValue("rua", Pg.NullableUtc(iv.ResumeUploadedAt));
                cmd.Parameters.AddWithValue("ca", SharedDbContext.Utc(iv.CreatedAt));
                cmd.Parameters.AddWithValue("ua", SharedDbContext.Utc(iv.UpdatedAt));
            });
    }

    public Task DeleteAsync(ObjectId id) =>
        ExecuteAsync("DELETE FROM ds_interviews WHERE user_id = @uid AND id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("id", Pg.Hex(id));
            });

    public async Task<bool> AnyForBidAsync(ObjectId bidId) =>
        await CountAsync("SELECT COUNT(*) FROM ds_interviews WHERE user_id = @uid AND bid_id = @bd",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("bd", Pg.Hex(bidId));
            }) > 0;

    public Task<long> CountByProfileAsync(ObjectId profileId) =>
        CountAsync("SELECT COUNT(*) FROM ds_interviews WHERE user_id = @uid AND profile_id = @pid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(profileId));
            });

    public Task<List<string>> ListCompaniesByProfileWithStatusAsync(
        ObjectId profileId, IReadOnlyCollection<string> statuses) =>
        ListAsync("SELECT company FROM ds_interviews WHERE user_id = @uid AND profile_id = @pid " +
                  "AND status = ANY(@ss)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(profileId));
                cmd.Parameters.Add(Pg.Array("ss", statuses));
            },
            r => Pg.Text(r, 0));

    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId) =>
        await ExecuteAsync(
            "UPDATE ds_interviews SET profile_id = @pid WHERE user_id = @uid AND profile_id = ''",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("pid", Pg.Hex(profileId));
            });

    public Task<List<Interview>> ListAllAsync() =>
        ListAsync($"SELECT {Cols} FROM ds_interviews WHERE user_id = @uid",
            cmd => cmd.Parameters.AddWithValue("uid", UserId), Map);

    public Task<List<Interview>> ListMissingProcessAsync() =>
        ListAsync($"SELECT {Cols} FROM ds_interviews WHERE user_id = @uid AND process_id = ''",
            cmd => cmd.Parameters.AddWithValue("uid", UserId), Map);

    public Task SetProcessIdAsync(ObjectId interviewId, ObjectId processId) =>
        ExecuteAsync("UPDATE ds_interviews SET process_id = @pr WHERE user_id = @uid AND id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("id", Pg.Hex(interviewId));
                cmd.Parameters.AddWithValue("pr", Pg.Hex(processId));
            });

    internal static Interview Map(NpgsqlDataReader r) => new()
    {
        Id = Pg.OidAt(r, 0),
        UserId = r.GetInt64(1),
        ProfileId = Pg.OidAt(r, 2),
        BidId = Pg.OidAt(r, 3),
        ParentInterviewId = Pg.NullableOidAt(r, 4),
        ProcessId = Pg.OidAt(r, 5),
        MeetingLink = Pg.Text(r, 6),
        Origin = Pg.Text(r, 7),
        InterviewType = Pg.Text(r, 8),
        Company = Pg.Text(r, 9),
        Role = Pg.Text(r, 10),
        Recruiter = Pg.Text(r, 11),
        AdditionalAttendees = Pg.StringsAt(r, 12),
        ResumeId = Pg.Text(r, 13),
        ScheduledDate = Pg.NullableDateAt(r, 14),
        ScheduledTime = Pg.Text(r, 15),
        DurationMinutes = Pg.NullableIntAt(r, 16),
        Status = Pg.Text(r, 17),
        UserComment = Pg.Text(r, 18),
        AttachedJobDescription = Pg.Text(r, 19),
        AttachedResumeContent = Pg.Text(r, 20),
        ResumeObjectKey = Pg.Text(r, 21),
        ResumeFileName = Pg.Text(r, 22),
        ResumeSizeBytes = r.IsDBNull(23) ? 0 : r.GetInt64(23),
        ResumeUploadedAt = Pg.NullableDateAt(r, 24),
        CreatedAt = r.GetDateTime(25),
        UpdatedAt = r.GetDateTime(26),
    };
}

/// <summary>
/// The rest of the team. Every query excludes the signed-in account, so "peers" keeps meaning
/// other people even though the rows now sit in the same tables as your own.
/// </summary>
public sealed class PgPeerDirectory : PgRepository, IPeerDirectory
{
    public PgPeerDirectory(SharedDbContext db, SessionContext session) : base(db, session) { }

    /// <summary>
    /// Everyone's identities, this account's included — the picker lists you alongside the team,
    /// and the Stats overview needs your label from the same source as everyone else's.
    /// </summary>
    public Task<List<PeerIdentity>> ListIdentitiesAsync() =>
        ListAsync("""
            SELECT u.user_id, u.username, p.id, p.name, p.slug, p.personal_email
            FROM ds_users u
            JOIN ds_profiles p ON p.user_id = u.user_id
            ORDER BY u.username, p.created_at
            """,
            _ => { },
            r => new PeerIdentity
            {
                UserId = r.GetInt64(0),
                Username = Pg.Text(r, 1),
                ProfileId = Pg.OidAt(r, 2),
                ProfileName = Pg.Text(r, 3),
                ProfileSlug = Pg.Text(r, 4),
                Email = Pg.Text(r, 5),
            });

    public Task<List<UserBid>> ListBidsUpdatedBetweenAsync(DateTime fromUtc, DateTime toUtc) =>
        ListAsync($"SELECT {PgBidRepository.Cols} FROM ds_bids WHERE user_id <> @uid " +
                  "AND updated_at >= @f AND updated_at < @t ORDER BY updated_at DESC",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("f", SharedDbContext.Utc(fromUtc));
                cmd.Parameters.AddWithValue("t", SharedDbContext.Utc(toUtc));
            },
            PgBidRepository.Map);

    public Task<List<UserBid>> ListNonDraftBidsByProfilesAsync(IReadOnlyCollection<ObjectId> profileIds) =>
        profileIds.Count == 0
            ? Task.FromResult(new List<UserBid>())
            : ListAsync($"SELECT {PgBidRepository.Cols} FROM ds_bids WHERE user_id <> @uid " +
                        "AND profile_id = ANY(@pids) AND status <> @draft",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("uid", UserId);
                    cmd.Parameters.Add(Pg.Array("pids", profileIds.Select(Pg.Hex)));
                    cmd.Parameters.AddWithValue("draft", BidStatuses.Draft);
                },
                PgBidRepository.Map);

    public Task<List<Interview>> ListInterviewsScheduledBetweenAsync(
        DateTime fromUtc, DateTime toUtc, bool includeUndated)
    {
        var window = includeUndated
            ? "(scheduled_date IS NULL OR (scheduled_date >= @f AND scheduled_date < @t))"
            : "(scheduled_date >= @f AND scheduled_date < @t)";

        return ListAsync(
            $"SELECT {PgInterviewRepository.Cols} FROM ds_interviews " +
            $"WHERE user_id <> @uid AND {window} ORDER BY scheduled_date",
            cmd =>
            {
                cmd.Parameters.AddWithValue("uid", UserId);
                cmd.Parameters.AddWithValue("f", SharedDbContext.Utc(fromUtc));
                cmd.Parameters.AddWithValue("t", SharedDbContext.Utc(toUtc));
            },
            PgInterviewRepository.Map);
    }
}
