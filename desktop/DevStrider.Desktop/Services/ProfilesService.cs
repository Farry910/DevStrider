using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

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

    public ProfilesService(
        IProfileRepository profiles,
        IBidRepository bids,
        IInterviewRepository interviews)
    {
        _profiles = profiles;
        _bids = bids;
        _interviews = interviews;
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
        // A vanished ds_users row (ds_profiles.user_id references it) is hr-system's problem to
        // repair, not this app's — every PUT /api/devstrider/profiles/:id retries once behind a
        // re-created account row before answering (see its dsWrite helper).
        await _profiles.UpsertAsync(p);
        return p;
    }

    public async Task UpdateAsync(Profile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await _profiles.UpsertAsync(profile);
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
