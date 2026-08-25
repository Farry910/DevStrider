using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// CRUD over the account's bidding identities. Different from <see cref="ProfileService"/>
/// (singular), which manages the one <c>ds_users</c> row behind the signed-in account.
/// </summary>
public class ProfilesService
{
    private readonly IProfileRepository _profiles;
    private readonly IBidRepository _bids;
    private readonly IInterviewRepository _interviews;
    private readonly ProfileService _account;

    public ProfilesService(
        IProfileRepository profiles,
        IBidRepository bids,
        IInterviewRepository interviews,
        ProfileService account)
    {
        _profiles = profiles;
        _bids = bids;
        _interviews = interviews;
        _account = account;
    }

    /// <summary>
    /// Run a write that depends on the account row, repairing that row once if the portal says it
    /// is missing.
    ///
    /// <para>
    /// This used to catch SQLSTATE 23503 from the driver: <c>ds_profiles.user_id</c> references
    /// <c>ds_users</c>, and a vanished account row surfaced as a foreign-key violation on a save
    /// the user had every reason to expect to work. The portal repairs that itself now — it holds
    /// the account the token names, so it can re-seat the row and retry without asking anyone.
    /// This is the belt to that server-side braces: a 409 or 500 that survives the server's own
    /// retry gets one attempt at putting the row back from here, which also covers a portal old
    /// enough not to do it. It costs one request on a path that is already broken and nothing at
    /// all on the normal one.
    /// </para>
    /// </summary>
    private async Task WithAccountRowAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (PortalApiException ex) when (ex.Status is 409 or 500)
        {
            await _account.EnsureRowAsync();
            await write();
        }
    }

    public Task<List<Profile>> ListAsync() => _profiles.ListAsync();

    public Task<Profile?> GetAsync(ObjectId id) => _profiles.GetAsync(id);

    /// <summary>
    /// The only place a <see cref="Profile"/> is constructed — the Profiles tab and first-run
    /// seeding both come through here.
    /// </summary>
    public async Task<Profile> CreateAsync(string name, string wordDocPath = "")
    {
        var p = new Profile
        {
            Name = (name ?? "").Trim(),
            WordDocPath = wordDocPath ?? "",
            // Every template ships with this entry point, so seed it rather than leaving the
            // field blank. Blank has always resolved to the same name at call time, but only
            // after the UI had shown an empty box that looked like something still to fill in.
            MacroName = WordMacroService.DefaultMacroName,
        };
        if (p.Name.Length == 0) p.Name = "Profile";
        await WithAccountRowAsync(() => _profiles.UpsertAsync(p));
        return p;
    }

    public async Task UpdateAsync(Profile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await WithAccountRowAsync(() => _profiles.UpsertAsync(profile));
    }

    public Task DeleteAsync(ObjectId id) => _profiles.DeleteAsync(id);

    /// <summary>
    /// Rows owned by a profile — the delete guard. There is no separate link count any more: a
    /// captured URL and the bid against it are the same row, so a posting with nothing bid on it
    /// is a draft bid and is counted as one.
    /// </summary>
    public async Task<(long bids, long interviews)> OwnedRowCountsAsync(ObjectId profileId)
    {
        var bids = await _bids.CountByProfileAsync(profileId);
        var interviews = await _interviews.CountByProfileAsync(profileId);
        return (bids, interviews);
    }
}
