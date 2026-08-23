using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Data;

/// <summary>
/// The seam between the services and the database.
///
/// <para>
/// One interface per aggregate, each exposing the queries the app actually issues rather than a
/// general query language. A repository that took LINQ expressions would need an ORM behind it;
/// one that returned everything would move the filtering into memory. Naming each query keeps the
/// business logic — the bid board's warning rules, the stats aggregation — in one place, and
/// leaves the SQL free to answer in whatever way suits it.
/// </para>
///
/// <para>
/// <see cref="ObjectId"/> is the identity type, stored as its 24-character hex string. The shape
/// came from the local MongoDB these tables replace; keeping it meant every existing row stayed
/// valid when the driver was dropped and the type moved into this project.
/// </para>
///
/// <para>
/// <b>Every implementation scopes to the logged-in account.</b> Callers never pass a user id and
/// cannot ask for someone else's rows — the repositories read it from
/// <see cref="Services.SessionContext"/>. The one deliberate exception is
/// <see cref="IPeerDirectory"/>, which exists precisely to read across the team.
/// </para>
/// </summary>
public interface IAccountRepository
{
    /// <summary>This account's row, or null before first login has seeded it.</summary>
    Task<UserProfile?> GetAsync();

    Task UpsertAsync(UserProfile account);

    /// <summary>
    /// Put the account row back if it has gone missing, leaving an existing one alone.
    ///
    /// <para>
    /// Login seeds it, so in the ordinary case this does nothing. It exists because the row can
    /// disappear underneath a live session — re-applying <c>shared-db-schema.sql</c> drops
    /// <c>ds_users</c> CASCADE — and every other table's foreign key points at it, so the first
    /// thing the user notices is a 23503 on a profile save they had no reason to expect to fail.
    /// </para>
    /// </summary>
    Task EnsureAsync(string username);

    /// <summary>
    /// Whether the name is already someone else's. Usernames are unique across the team, so the
    /// Settings form has to check before saving rather than surfacing a constraint violation.
    /// </summary>
    Task<bool> UsernameTakenAsync(string username);
}

/// <summary>
/// Bidding identities — the title-bar profile switcher.
///
/// <para>
/// A profile is one row. It used to be four: education, certifications and work history hung off
/// it as child tables, which meant every save was a transaction that rewrote them. That material
/// lives in the profile's .docm now and none of it is kept here — the app never reads a CV and
/// never renders one.
/// </para>
/// </summary>
public interface IProfileRepository
{
    /// <summary>Oldest first — the order the switcher and Profiles tab both show.</summary>
    Task<List<Profile>> ListAsync();

    Task<Profile?> GetAsync(ObjectId id);

    /// <summary>Insert or replace. Idempotent on <see cref="Profile.Id"/>.</summary>
    Task UpsertAsync(Profile profile);

    Task DeleteAsync(ObjectId id);
}

/// <summary>
/// Job postings and the bids made against them — one row each, see <see cref="UserBid"/>.
///
/// <para>
/// Counts come in two flavours and the difference matters: <see cref="CountByProfileAsync"/> is
/// every row, which is every posting captured, while the non-draft counts are bids actually sent.
/// The Overview table shows both side by side.
/// </para>
/// </summary>
public interface IBidRepository
{
    Task<List<UserBid>> ListByProfileAsync(ObjectId profileId);

    /// <summary>
    /// The dedup lookup, hit on every capture from the UI and the Chrome extension. Scoped to one
    /// profile so the same posting under a different identity doesn't collide.
    /// </summary>
    Task<UserBid?> FindByUrlNormAsync(ObjectId profileId, string urlNorm);

    Task<UserBid?> GetAsync(ObjectId id);
    Task UpsertAsync(UserBid bid);
    Task DeleteAsync(ObjectId id);

    /// <summary>Every row under the profile — postings captured, bid on or not.</summary>
    Task<long> CountByProfileAsync(ObjectId profileId);

    /// <summary>Postings captured in the window.</summary>
    Task<long> CountCreatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc);

    /// <summary>Newest first — the Find-bid search window.</summary>
    Task<List<UserBid>> ListByProfileUpdatedSinceAsync(ObjectId profileId, DateTime sinceUtc);

    Task<List<UserBid>> ListByProfileUpdatedBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc);

    /// <summary>Non-draft rows under one profile — the bids-per-10-min chart buckets these itself.</summary>
    Task<List<UserBid>> ListNonDraftByProfileAsync(ObjectId profileId);

    /// <summary>
    /// Stamp the given profile onto this account's rows that have none. Legacy-data repair for
    /// the single-profile → multi-profile move; returns how many rows were touched.
    /// </summary>
    Task<long> AssignProfileToUnassignedAsync(ObjectId profileId);
}

public interface IInterviewRepository
{
    /// <summary>Scheduled inside the window, earliest first.</summary>
    Task<List<Interview>> ListByProfileScheduledBetweenAsync(ObjectId profileId, DateTime fromUtc, DateTime toUtc);

    Task UpsertAsync(Interview interview);
    Task DeleteAsync(ObjectId id);
    Task<bool> AnyForBidAsync(ObjectId bidId);
    Task<long> CountByProfileAsync(ObjectId profileId);

    /// <summary>
    /// Company names of this profile's interviews in the given statuses — feeds the bid board's
    /// "you already interview here" warning. Only the column that warning needs is read.
    /// </summary>
    Task<List<string>> ListCompaniesByProfileWithStatusAsync(ObjectId profileId, IReadOnlyCollection<string> statuses);


    // ── legacy-data repair ──────────────────────────────────────────────────
    Task<long> AssignProfileToUnassignedAsync(ObjectId profileId);
    Task<List<Interview>> ListAllAsync();
    Task<List<Interview>> ListMissingProcessAsync();
    Task SetProcessIdAsync(ObjectId interviewId, ObjectId processId);
}

/// <summary>
/// The team's view — everyone else's rows in the same tables.
///
/// <para>
/// This used to read a local mirror that a sync service pulled down from stripped
/// <c>peer_*</c> tables. There is nothing left to mirror: a teammate's bid is a
/// <see cref="UserBid"/> with a different <see cref="UserBid.UserId"/>, sitting in the same table
/// as yours. Every method here excludes the logged-in account, so "peers" still means other
/// people and the Peers tab is live rather than as fresh as the last sync.
/// </para>
/// </summary>
public interface IPeerDirectory
{
    /// <summary>Every (person, profile) pair on the team, this account's own included.</summary>
    Task<List<PeerIdentity>> ListIdentitiesAsync();

    Task<List<UserBid>> ListBidsUpdatedBetweenAsync(DateTime fromUtc, DateTime toUtc);

    Task<List<UserBid>> ListNonDraftBidsByProfilesAsync(IReadOnlyCollection<ObjectId> profileIds);

    /// <summary>
    /// Interviews in the window. <paramref name="includeUndated"/> keeps rows with no scheduled
    /// date, which the Peers tab shows and the Stats overview does not.
    /// </summary>
    Task<List<Interview>> ListInterviewsScheduledBetweenAsync(
        DateTime fromUtc, DateTime toUtc, bool includeUndated);
}


/// <summary>
/// Personal reference data — <c>ds_person_facts</c>: education, career history and custom fields.
///
/// <para>
/// Everything <c>ds_profiles</c> already holds is absent here on purpose. That row is shared with
/// the company portal, which allocates profiles to people and reports on them, so name, location,
/// phone, email and LinkedIn have exactly one home and it is not this table.
/// </para>
/// </summary>
public interface IPersonFactRepository
{
    Task<List<PersonFact>> ListByProfileAsync(ObjectId profileId);

    /// <summary>Replaces the profile's facts wholesale — the editor saves a complete picture.</summary>
    Task ReplaceForProfileAsync(ObjectId profileId, IReadOnlyCollection<PersonFact> facts);
}
