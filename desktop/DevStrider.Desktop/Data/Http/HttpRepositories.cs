using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Services.HrApi;
using MongoDB.Bson;

namespace DevStrider.Desktop.Data.Http;

// Every repository here is a translation, not a store: the account is the token's (see
// HrApiClient), so nothing below ever puts a user id on the wire. That is the one behavioural
// change from the Postgres repositories these replace — there, SessionContext.Require() was read
// into every query by hand; here, hr-system reads it off the bearer token instead, and a request
// made before sign-in fails at HrApiClient with HrApiNotSignedInException rather than at the
// database with user_id = 0.

internal static class Iso
{
    public static string Utc(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");
}

/// <summary>The <c>ds_users</c> row for the signed-in account, read and written over HTTP.</summary>
public sealed class HttpAccountRepository : IAccountRepository
{
    private readonly HrApiClient _api;

    public HttpAccountRepository(HrApiClient api) => _api = api;

    public async Task<UserProfile?> GetAsync()
    {
        var dto = await _api.GetAsync<AccountDto?>("/api/devstrider/account");
        return dto == null ? null : Map(dto);
    }

    public async Task UpsertAsync(UserProfile a)
    {
        await _api.PutAsync<AccountDto>("/api/devstrider/account", new { username = a.Username ?? "" });
    }

    /// <summary>
    /// Put the row back if it has gone missing, leaving an existing one alone. hr-system's PUT
    /// always overwrites the username, so this checks first rather than delegating to it — the
    /// point of "ensure" is specifically to not re-assert a name a session might be stale about.
    /// </summary>
    public async Task EnsureAsync(string username)
    {
        if (await GetAsync() != null) return;
        await _api.PutAsync<AccountDto>("/api/devstrider/account", new { username = username ?? "" });
    }

    public async Task<bool> UsernameTakenAsync(string username)
    {
        var result = await _api.GetAsync<UsernameTakenDto>(
            "/api/devstrider/account/username-taken",
            new Dictionary<string, string?> { ["username"] = username ?? "" });
        return result.Taken;
    }

    private static UserProfile Map(AccountDto d) => new()
    {
        UserId = d.UserId,
        Username = d.Username,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}

/// <summary>Bidding identities — the title-bar profile switcher, read and written over HTTP.</summary>
public sealed class HttpProfileRepository : IProfileRepository
{
    private readonly HrApiClient _api;

    public HttpProfileRepository(HrApiClient api) => _api = api;

    public async Task<List<Profile>> ListAsync()
    {
        var dtos = await _api.GetAsync<List<ProfileDto>>("/api/devstrider/profiles");
        return dtos.Select(Map).ToList();
    }

    public async Task<Profile?> GetAsync(ObjectId id)
    {
        try
        {
            var dto = await _api.GetAsync<ProfileDto>($"/api/devstrider/profiles/{Hex(id)}");
            return Map(dto);
        }
        catch (HrApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertAsync(Profile p)
    {
        await _api.PutAsync<ProfileDto>($"/api/devstrider/profiles/{Hex(p.Id)}", new
        {
            name = p.Name ?? "",
            wordDocPath = p.WordDocPath ?? "",
            macroName = p.MacroName ?? "",
            resumePrompt = p.ResumePrompt ?? "",
            headline = p.Headline ?? "",
            location = p.Location ?? "",
            phone = p.Phone ?? "",
            personalEmail = p.PersonalEmail ?? "",
            linkedinUrl = p.LinkedinUrl ?? "",
            createdAt = Iso.Utc(p.CreatedAt),
        });
    }

    public async Task DeleteAsync(ObjectId id) =>
        await _api.DeleteAsync<DeletedCountDto>($"/api/devstrider/profiles/{Hex(id)}");

    private static string Hex(ObjectId id) => id.ToString();

    private static Profile Map(ProfileDto d) => new()
    {
        Id = d.Id,
        UserId = d.UserId,
        Name = d.Name,
        WordDocPath = d.WordDocPath,
        MacroName = d.MacroName,
        ResumePrompt = d.ResumePrompt,
        Headline = d.Headline,
        Location = d.Location,
        Phone = d.Phone,
        PersonalEmail = d.PersonalEmail,
        LinkedinUrl = d.LinkedinUrl,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}

/// <summary>Job postings and the bids made against them, read and written over HTTP.</summary>
public sealed class HttpBidRepository : IBidRepository
{
    private readonly HrApiClient _api;

    public HttpBidRepository(HrApiClient api) => _api = api;

    public async Task<List<UserBid>> ListByProfileAsync(ObjectId profileId) =>
        (await ListAsync(new() { ["profileId"] = Hex(profileId) })).Select(Map).ToList();

    public async Task<UserBid?> FindByUrlNormAsync(ObjectId profileId, string urlNorm)
    {
        var rows = await ListAsync(new()
        {
            ["profileId"] = Hex(profileId),
            ["urlNorm"] = urlNorm ?? "",
        });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task<UserBid?> GetAsync(ObjectId id)
    {
        try
        {
            var dto = await _api.GetAsync<BidDto>($"/api/devstrider/bids/{Hex(id)}");
            return Map(dto);
        }
        catch (HrApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertAsync(UserBid b) =>
        await _api.PutAsync<BidDto>($"/api/devstrider/bids/{Hex(b.Id)}", new
        {
            profileId = Hex(b.ProfileId),
            url = b.Url ?? "",
            urlNorm = b.UrlNorm ?? "",
            markedUselessAt = b.MarkedUselessAt is { } mu ? Iso.Utc(mu) : null,
            resumeId = b.ResumeId ?? "",
            company = b.Company ?? "",
            role = b.Role ?? "",
            primaryStacks = b.PrimaryStacks ?? new List<string>(),
            status = string.IsNullOrEmpty(b.Status) ? BidStatuses.Draft : b.Status,
            origin = b.Origin ?? "",
            jobDescription = b.JobDescription ?? "",
            gptResumeContent = b.GptResumeContent ?? "",
            comment = b.Comment ?? "",
            createdAt = Iso.Utc(b.CreatedAt),
            updatedAt = Iso.Utc(b.UpdatedAt),
            appliedAt = b.AppliedAt is { } aa ? Iso.Utc(aa) : null,
        });

    public async Task DeleteAsync(ObjectId id) =>
        await _api.DeleteAsync<DeletedCountDto>($"/api/devstrider/bids/{Hex(id)}");

    public async Task<long> CountByProfileAsync(ObjectId profileId)
    {
        var r = await _api.GetAsync<BidCountDto>("/api/devstrider/bids/count",
            new Dictionary<string, string?> { ["profileId"] = Hex(profileId) });
        return r.Count;
    }

    public async Task<long> CountCreatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc)
    {
        var r = await _api.GetAsync<BidCountDto>("/api/devstrider/bids/count", new Dictionary<string, string?>
        {
            ["profileId"] = Hex(profileId),
            ["createdFrom"] = Iso.Utc(fromUtc),
            ["createdTo"] = Iso.Utc(toUtc),
        });
        return r.Count;
    }

    public async Task<List<UserBid>> ListByProfileUpdatedSinceAsync(ObjectId profileId, DateTime sinceUtc) =>
        (await ListAsync(new()
        {
            ["profileId"] = Hex(profileId),
            ["updatedFrom"] = Iso.Utc(sinceUtc),
        })).Select(Map).ToList();

    public async Task<List<UserBid>> ListByProfileUpdatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        (await ListAsync(new()
        {
            ["profileId"] = Hex(profileId),
            ["updatedFrom"] = Iso.Utc(fromUtc),
            ["updatedTo"] = Iso.Utc(toUtc),
        })).Select(Map).ToList();

    public async Task<List<UserBid>> ListNonDraftByProfileAsync(ObjectId profileId) =>
        (await ListAsync(new()
        {
            ["profileId"] = Hex(profileId),
            ["nonDraft"] = "true",
        })).Select(Map).ToList();

    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId)
    {
        var r = await _api.PostAsync<ChangedCountDto>("/api/devstrider/bids/assign-profile",
            new { profileId = Hex(profileId) });
        return r.Changed;
    }

    private Task<List<BidDto>> ListAsync(Dictionary<string, string?> query) =>
        _api.GetAsync<List<BidDto>>("/api/devstrider/bids", query);

    internal static string Hex(ObjectId id) => id.ToString();

    internal static UserBid Map(BidDto d) => new()
    {
        Id = d.Id,
        UserId = d.UserId,
        ProfileId = d.ProfileId,
        Url = d.Url,
        UrlNorm = d.UrlNorm,
        MarkedUselessAt = d.MarkedUselessAt,
        ResumeId = d.ResumeId,
        Company = d.Company,
        Role = d.Role,
        PrimaryStacks = d.PrimaryStacks,
        Status = d.Status,
        Origin = d.Origin,
        JobDescription = d.JobDescription,
        GptResumeContent = d.GptResumeContent,
        Comment = d.Comment,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        AppliedAt = d.AppliedAt,
    };
}

public sealed class HttpInterviewRepository : IInterviewRepository
{
    private readonly HrApiClient _api;

    public HttpInterviewRepository(HrApiClient api) => _api = api;

    public async Task<List<Interview>> ListByProfileScheduledBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        (await ListAsync(new()
        {
            ["profileId"] = Hex(profileId),
            ["scheduledFrom"] = Iso.Utc(fromUtc),
            ["scheduledTo"] = Iso.Utc(toUtc),
        })).Select(Map).ToList();

    public async Task UpsertAsync(Interview iv) =>
        await _api.PutAsync<InterviewDto>($"/api/devstrider/interviews/{Hex(iv.Id)}", new
        {
            profileId = Hex(iv.ProfileId),
            bidId = Hex(iv.BidId),
            parentInterviewId = iv.ParentInterviewId is { } par ? Hex(par) : null,
            processId = Hex(iv.ProcessId),
            meetingLink = iv.MeetingLink ?? "",
            origin = iv.Origin ?? "",
            interviewType = iv.InterviewType ?? "",
            company = iv.Company ?? "",
            role = iv.Role ?? "",
            recruiter = iv.Recruiter ?? "",
            additionalAttendees = iv.AdditionalAttendees ?? new List<string>(),
            resumeId = iv.ResumeId ?? "",
            scheduledDate = iv.ScheduledDate is { } sd ? Iso.Utc(sd) : null,
            scheduledTime = iv.ScheduledTime ?? "",
            durationMinutes = iv.DurationMinutes,
            status = iv.Status ?? "",
            userComment = iv.UserComment ?? "",
            attachedJobDescription = iv.AttachedJobDescription ?? "",
            attachedResumeContent = iv.AttachedResumeContent ?? "",
            resumeObjectKey = iv.ResumeObjectKey ?? "",
            resumeFileName = iv.ResumeFileName ?? "",
            resumeSizeBytes = iv.ResumeSizeBytes,
            resumeUploadedAt = iv.ResumeUploadedAt is { } ru ? Iso.Utc(ru) : null,
            createdAt = Iso.Utc(iv.CreatedAt),
            updatedAt = Iso.Utc(iv.UpdatedAt),
        });

    public async Task DeleteAsync(ObjectId id) =>
        await _api.DeleteAsync<DeletedCountDto>($"/api/devstrider/interviews/{Hex(id)}");

    public async Task<bool> AnyForBidAsync(ObjectId bidId)
    {
        var r = await _api.GetAsync<InterviewCountDto>("/api/devstrider/interviews/count",
            new Dictionary<string, string?> { ["bidId"] = Hex(bidId) });
        return r.Count > 0;
    }

    public async Task<long> CountByProfileAsync(ObjectId profileId)
    {
        var r = await _api.GetAsync<InterviewCountDto>("/api/devstrider/interviews/count",
            new Dictionary<string, string?> { ["profileId"] = Hex(profileId) });
        return r.Count;
    }

    public async Task<List<string>> ListCompaniesByProfileWithStatusAsync(ObjectId profileId, IReadOnlyCollection<string> statuses) =>
        await _api.GetAsync<List<string>>("/api/devstrider/interviews/companies", new Dictionary<string, string?>
        {
            ["profileId"] = Hex(profileId),
            ["statuses"] = string.Join(",", statuses ?? Array.Empty<string>()),
        });

    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId)
    {
        var r = await _api.PostAsync<ChangedCountDto>("/api/devstrider/interviews/assign-profile",
            new { profileId = Hex(profileId) });
        return r.Changed;
    }

    public async Task<List<Interview>> ListAllAsync() =>
        (await ListAsync(new())).Select(Map).ToList();

    public async Task<List<Interview>> ListMissingProcessAsync() =>
        (await ListAsync(new() { ["missingProcess"] = "true" })).Select(Map).ToList();

    public async Task SetProcessIdAsync(ObjectId interviewId, ObjectId processId) =>
        await _api.PutAsync<ChangedCountDto>($"/api/devstrider/interviews/{Hex(interviewId)}/process",
            new { processId = Hex(processId) });

    private Task<List<InterviewDto>> ListAsync(Dictionary<string, string?> query) =>
        _api.GetAsync<List<InterviewDto>>("/api/devstrider/interviews", query);

    internal static string Hex(ObjectId id) => id.ToString();

    internal static Interview Map(InterviewDto d) => new()
    {
        Id = d.Id,
        UserId = d.UserId,
        ProfileId = d.ProfileId,
        BidId = d.BidId,
        ParentInterviewId = d.ParentInterviewId,
        ProcessId = d.ProcessId,
        MeetingLink = d.MeetingLink,
        Origin = d.Origin,
        InterviewType = d.InterviewType,
        Company = d.Company,
        Role = d.Role,
        Recruiter = d.Recruiter,
        AdditionalAttendees = d.AdditionalAttendees,
        ResumeId = d.ResumeId,
        ScheduledDate = d.ScheduledDate,
        ScheduledTime = d.ScheduledTime,
        DurationMinutes = d.DurationMinutes,
        Status = d.Status,
        UserComment = d.UserComment,
        AttachedJobDescription = d.AttachedJobDescription,
        AttachedResumeContent = d.AttachedResumeContent,
        ResumeObjectKey = d.ResumeObjectKey,
        ResumeFileName = d.ResumeFileName,
        ResumeSizeBytes = d.ResumeSizeBytes,
        ResumeUploadedAt = d.ResumeUploadedAt,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}

/// <summary>The rest of the team — every query here reads across accounts by design.</summary>
public sealed class HttpPeerDirectory : IPeerDirectory
{
    private readonly HrApiClient _api;

    public HttpPeerDirectory(HrApiClient api) => _api = api;

    public async Task<List<PeerIdentity>> ListIdentitiesAsync()
    {
        var dtos = await _api.GetAsync<List<PeerIdentityDto>>("/api/devstrider/peers/identities");
        return dtos.Select(d => new PeerIdentity
        {
            UserId = d.UserId,
            Username = d.Username,
            ProfileId = d.ProfileId,
            ProfileName = d.ProfileName,
            ProfileSlug = d.ProfileSlug,
            Email = d.Email,
        }).ToList();
    }

    public async Task<List<UserBid>> ListBidsUpdatedBetweenAsync(DateTime fromUtc, DateTime toUtc)
    {
        var dtos = await _api.GetAsync<List<BidDto>>("/api/devstrider/peers/bids", new Dictionary<string, string?>
        {
            ["updatedFrom"] = Iso.Utc(fromUtc),
            ["updatedTo"] = Iso.Utc(toUtc),
        });
        return dtos.Select(HttpBidRepository.Map).ToList();
    }

    public async Task<List<UserBid>> ListNonDraftBidsByProfilesAsync(IReadOnlyCollection<ObjectId> profileIds)
    {
        if (profileIds.Count == 0) return new List<UserBid>();
        var dtos = await _api.GetAsync<List<BidDto>>("/api/devstrider/peers/bids", new Dictionary<string, string?>
        {
            ["profileIds"] = string.Join(",", profileIds.Select(HttpBidRepository.Hex)),
            ["nonDraft"] = "true",
        });
        return dtos.Select(HttpBidRepository.Map).ToList();
    }

    public async Task<List<Interview>> ListInterviewsScheduledBetweenAsync(DateTime fromUtc, DateTime toUtc, bool includeUndated)
    {
        var query = new Dictionary<string, string?>
        {
            ["from"] = Iso.Utc(fromUtc),
            ["to"] = Iso.Utc(toUtc),
        };
        if (includeUndated) query["includeUndated"] = "true";
        var dtos = await _api.GetAsync<List<InterviewDto>>("/api/devstrider/peers/interviews", query);
        return dtos.Select(HttpInterviewRepository.Map).ToList();
    }
}
