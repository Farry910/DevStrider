using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.Data.Http;

// The repository interfaces did not change when the store moved out from under them, and that is
// the whole point of their existing: every service and view-model above this line is untouched by
// the switch from "open a connection and run SQL" to "send a request". What changed is that the
// user id is no longer something this side supplies — the server takes it off the bearer token and
// pins every read and write to it, so the scoping that used to be a `WHERE user_id = @uid` this
// app was trusted to remember is now enforced where it cannot be forgotten.
//
// UserId is still read from the session on this side, for one reason: to fail loudly and locally
// when something calls a repository before login. A request sent with no token would come back a
// 401, which reads as a broken server rather than as a bug in the caller.

/// <summary>Shared plumbing: the client, and the guard that there is somebody signed in.</summary>
public abstract class ApiRepository
{
    protected readonly PortalApi Api;
    private readonly SessionContext _session;

    protected ApiRepository(PortalApi api, SessionContext session)
    {
        Api = api;
        _session = session;
    }

    /// <summary>The signed-in <c>app_user.id</c>. Throws if there isn't one.</summary>
    protected long UserId => _session.Require();

    /// <summary>An id as the API spells it — 24 hex characters, or empty for "none".</summary>
    protected static string Hex(ObjectId id) => id.IsEmpty ? "" : id.ToString();

    /// <summary>Path segment for an id. Empty is not addressable, and asking for it is a bug.</summary>
    protected static string Segment(ObjectId id) =>
        id.IsEmpty ? throw new InvalidOperationException("An empty id has no row to address.") : id.ToString();

    protected static string Utc(DateTime value) =>
        UtcDateTimeConverter.Utc(value).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The <c>ds_users</c> row for the signed-in account.</summary>
public sealed class ApiAccountRepository : ApiRepository, IAccountRepository
{
    public ApiAccountRepository(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<UserProfile?> GetAsync()
    {
        _ = UserId;
        return Api.GetAsync<UserProfile>("/api/devstrider/account");
    }

    /// <summary>
    /// Rarely called. Sign-in writes this row at the portal, and there is nothing left on it the
    /// app edits afterwards — the username is the portal's address and is re-asserted from there
    /// on every sign-in and every session check.
    /// </summary>
    public Task UpsertAsync(UserProfile account)
    {
        account.UserId = UserId;
        return Api.PutAsync<UserProfile>("/api/devstrider/account", new { username = account.Username ?? "" });
    }

    public Task EnsureAsync(string username)
    {
        _ = UserId;
        return Api.PutAsync<UserProfile>("/api/devstrider/account", new { username = username ?? "" });
    }

    public async Task<bool> UsernameTakenAsync(string username)
    {
        _ = UserId;
        var answer = await Api.GetAsync<TakenResponse>(
            "/api/devstrider/account/username-taken" + PortalApi.Query(("username", username ?? "")));
        return answer?.Taken ?? false;
    }

    private sealed class TakenResponse { public bool Taken { get; set; } }
}

/// <summary>Bidding identities. One row each — the CV lives in the profile's .docm.</summary>
public sealed class ApiProfileRepository : ApiRepository, IProfileRepository
{
    public ApiProfileRepository(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<List<Profile>> ListAsync()
    {
        _ = UserId;
        return Api.ListAsync<Profile>("/api/devstrider/profiles");
    }

    /// <summary>
    /// A row that isn't there is null, not an error. The server answers 404 for it, which is the
    /// right shape for a URL and the wrong shape for a lookup that legitimately misses — the
    /// caller asking "is this profile still around" gets an answer rather than an exception.
    /// </summary>
    public Task<Profile?> GetAsync(ObjectId id) =>
        Missing.AsNull(() => Api.GetAsync<Profile>($"/api/devstrider/profiles/{Segment(id)}"));

    public Task UpsertAsync(Profile profile)
    {
        profile.UserId = UserId;
        profile.UpdatedAt = DateTime.UtcNow;
        return Api.PutAsync<Profile>($"/api/devstrider/profiles/{Segment(profile.Id)}", profile);
    }

    public Task DeleteAsync(ObjectId id)
    {
        _ = UserId;
        return Api.DeleteAsync($"/api/devstrider/profiles/{Segment(id)}");
    }
}

public sealed class ApiBidRepository : ApiRepository, IBidRepository
{
    public ApiBidRepository(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<List<UserBid>> ListByProfileAsync(ObjectId profileId) =>
        List(("profileId", Hex(profileId)));

    /// <summary>
    /// The dedup lookup, hit on every capture from the UI and from the Chrome extension. Scoped to
    /// one profile so the same posting under a different identity doesn't collide.
    /// </summary>
    public async Task<UserBid?> FindByUrlNormAsync(ObjectId profileId, string urlNorm) =>
        (await List(("profileId", Hex(profileId)), ("urlNorm", urlNorm ?? ""))).FirstOrDefault();

    public Task<UserBid?> GetAsync(ObjectId id) =>
        Missing.AsNull(() => Api.GetAsync<UserBid>($"/api/devstrider/bids/{Segment(id)}"));

    public Task UpsertAsync(UserBid bid)
    {
        bid.UserId = UserId;
        return Api.PutAsync<UserBid>($"/api/devstrider/bids/{Segment(bid.Id)}", bid);
    }

    public Task DeleteAsync(ObjectId id)
    {
        _ = UserId;
        return Api.DeleteAsync($"/api/devstrider/bids/{Segment(id)}");
    }

    public Task<long> CountByProfileAsync(ObjectId profileId) =>
        Count(("profileId", Hex(profileId)));

    public Task<long> CountCreatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        Count(("profileId", Hex(profileId)), ("createdFrom", Utc(fromUtc)), ("createdTo", Utc(toUtc)));

    public Task<List<UserBid>> ListByProfileUpdatedSinceAsync(ObjectId profileId, DateTime sinceUtc) =>
        List(("profileId", Hex(profileId)), ("updatedFrom", Utc(sinceUtc)));

    public Task<List<UserBid>> ListByProfileUpdatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        List(("profileId", Hex(profileId)), ("updatedFrom", Utc(fromUtc)), ("updatedTo", Utc(toUtc)));

    public Task<List<UserBid>> ListNonDraftByProfileAsync(ObjectId profileId) =>
        List(("profileId", Hex(profileId)), ("nonDraft", "true"));

    /// <summary>
    /// Stamp the given profile onto this account's rows that have none. Legacy-data repair for the
    /// single-profile → multi-profile move; returns how many rows were touched.
    /// </summary>
    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId)
    {
        _ = UserId;
        var result = await Api.PostAsync<ChangedResponse>("/api/devstrider/bids/assign-profile",
            new { profileId = Hex(profileId) });
        return result?.Changed ?? 0;
    }

    private Task<List<UserBid>> List(params (string, string?)[] filters)
    {
        _ = UserId;
        return Api.ListAsync<UserBid>("/api/devstrider/bids" + PortalApi.Query(filters));
    }

    private async Task<long> Count(params (string, string?)[] filters)
    {
        _ = UserId;
        var result = await Api.GetAsync<CountResponse>("/api/devstrider/bids/count" + PortalApi.Query(filters));
        return result?.Count ?? 0;
    }
}

public sealed class ApiInterviewRepository : ApiRepository, IInterviewRepository
{
    public ApiInterviewRepository(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<List<Interview>> ListByProfileScheduledBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc) =>
        List(("profileId", Hex(profileId)), ("scheduledFrom", Utc(fromUtc)), ("scheduledTo", Utc(toUtc)));

    public Task UpsertAsync(Interview interview)
    {
        interview.UserId = UserId;
        return Api.PutAsync<Interview>($"/api/devstrider/interviews/{Segment(interview.Id)}", interview);
    }

    public Task DeleteAsync(ObjectId id)
    {
        _ = UserId;
        return Api.DeleteAsync($"/api/devstrider/interviews/{Segment(id)}");
    }

    public async Task<bool> AnyForBidAsync(ObjectId bidId) =>
        await Count(("bidId", Hex(bidId))) > 0;

    public Task<long> CountByProfileAsync(ObjectId profileId) =>
        Count(("profileId", Hex(profileId)));

    public async Task<List<string>> ListCompaniesByProfileWithStatusAsync(
        ObjectId profileId, IReadOnlyCollection<string> statuses)
    {
        _ = UserId;
        if (statuses.Count == 0) return new List<string>();
        return await Api.ListAsync<string>("/api/devstrider/interviews/companies" + PortalApi.Query(
            ("profileId", Hex(profileId)), ("statuses", string.Join(",", statuses))));
    }

    // ── legacy-data repair ──────────────────────────────────────────────────

    public async Task<long> AssignProfileToUnassignedAsync(ObjectId profileId)
    {
        _ = UserId;
        var result = await Api.PostAsync<ChangedResponse>("/api/devstrider/interviews/assign-profile",
            new { profileId = Hex(profileId) });
        return result?.Changed ?? 0;
    }

    public Task<List<Interview>> ListAllAsync() => List();

    public Task<List<Interview>> ListMissingProcessAsync() => List(("missingProcess", "true"));

    public Task SetProcessIdAsync(ObjectId interviewId, ObjectId processId)
    {
        _ = UserId;
        return Api.PutAsync<ChangedResponse>($"/api/devstrider/interviews/{Segment(interviewId)}/process",
            new { processId = Hex(processId) });
    }

    private Task<List<Interview>> List(params (string, string?)[] filters)
    {
        _ = UserId;
        return Api.ListAsync<Interview>("/api/devstrider/interviews" + PortalApi.Query(filters));
    }

    private async Task<long> Count(params (string, string?)[] filters)
    {
        _ = UserId;
        var result = await Api.GetAsync<CountResponse>("/api/devstrider/interviews/count" + PortalApi.Query(filters));
        return result?.Count ?? 0;
    }
}

/// <summary>
/// The team's view — everyone else's rows in the same tables.
///
/// <para>
/// Every method here reads across accounts by design; it is the Peers tab, and the server excludes
/// the caller so "peers" keeps meaning other people. Identities are the deliberate exception: the
/// picker lists you alongside the team, and the Stats overview wants your label from the same
/// source as everyone else's.
/// </para>
/// </summary>
public sealed class ApiPeerDirectory : ApiRepository, IPeerDirectory
{
    public ApiPeerDirectory(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<List<PeerIdentity>> ListIdentitiesAsync()
    {
        _ = UserId;
        return Api.ListAsync<PeerIdentity>("/api/devstrider/peers/identities");
    }

    public Task<List<UserBid>> ListBidsUpdatedBetweenAsync(DateTime fromUtc, DateTime toUtc)
    {
        _ = UserId;
        return Api.ListAsync<UserBid>("/api/devstrider/peers/bids" + PortalApi.Query(
            ("updatedFrom", Utc(fromUtc)), ("updatedTo", Utc(toUtc))));
    }

    public Task<List<UserBid>> ListNonDraftBidsByProfilesAsync(IReadOnlyCollection<ObjectId> profileIds)
    {
        _ = UserId;
        // No profiles is no rows, and it is worth not asking: an empty ?profileIds= would read as
        // "no filter" and fetch the whole team's non-draft bids.
        if (profileIds.Count == 0) return Task.FromResult(new List<UserBid>());
        return Api.ListAsync<UserBid>("/api/devstrider/peers/bids" + PortalApi.Query(
            ("profileIds", string.Join(",", profileIds.Select(Hex).Where(id => id.Length > 0))),
            ("nonDraft", "true")));
    }

    public Task<List<Interview>> ListInterviewsScheduledBetweenAsync(DateTime fromUtc, DateTime toUtc, bool includeUndated)
    {
        _ = UserId;
        return Api.ListAsync<Interview>("/api/devstrider/peers/interviews" + PortalApi.Query(
            ("from", Utc(fromUtc)), ("to", Utc(toUtc)), ("includeUndated", includeUndated ? "true" : "false")));
    }
}

/// <summary>Personal reference data. Holds only what <c>ds_profiles</c> does not.</summary>
public sealed class ApiPersonFactRepository : ApiRepository, IPersonFactRepository
{
    public ApiPersonFactRepository(PortalApi api, SessionContext session) : base(api, session) { }

    public Task<List<PersonFact>> ListByProfileAsync(ObjectId profileId)
    {
        _ = UserId;
        return Api.ListAsync<PersonFact>("/api/devstrider/person-facts" + PortalApi.Query(
            ("profileId", Hex(profileId))));
    }

    /// <summary>
    /// Replaces the profile's facts wholesale — the editor saves a complete picture, and an
    /// education row the user removed has to actually disappear rather than linger and go on being
    /// fed to ChatGPT. One request, so the server can do it in one transaction.
    /// </summary>
    public Task ReplaceForProfileAsync(ObjectId profileId, IReadOnlyCollection<PersonFact> facts)
    {
        var userId = UserId;
        foreach (var fact in facts)
        {
            fact.UserId = userId;
            fact.ProfileId = profileId;
            fact.UpdatedAt = DateTime.UtcNow;
        }
        return Api.PutAsync<List<PersonFact>>("/api/devstrider/person-facts",
            new { profileId = Hex(profileId), facts });
    }
}

// ── shared response shapes ──────────────────────────────────────────────────

internal sealed class CountResponse { public long Count { get; set; } }
internal sealed class ChangedResponse { public long Changed { get; set; } }

/// <summary>
/// "Not found" is a 404 over HTTP and a null over SQL, and the repository contract is the null.
/// This is the one place that translates, so no caller has to know a request was involved.
/// </summary>
internal static class Missing
{
    public static async Task<T?> AsNull<T>(Func<Task<T?>> work) where T : class
    {
        try { return await work(); }
        catch (PortalApiException ex) when (ex.Status == 404) { return null; }
    }
}
