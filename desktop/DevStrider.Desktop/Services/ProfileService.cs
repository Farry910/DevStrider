using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The signed-in account's <c>ds_users</c> row — the name the rest of the team sees, and little else.
///
/// <para>
/// Distinct from <see cref="ProfilesService"/> (plural), which manages the bidding identities. One
/// account, several of those; everything genuinely per-person-behind-the-keyboard lives here.
/// </para>
///
/// <para>
/// The row is created by <see cref="AuthService"/> on first successful login, so by the time any
/// of this runs it exists. <see cref="GetAsync"/> still tolerates its absence rather than
/// throwing — a missing row should degrade to a blank name, not to a dead Settings tab.
/// </para>
/// </summary>
public class ProfileService
{
    private readonly IAccountRepository _accounts;
    private readonly SessionContext _session;

    public ProfileService(IAccountRepository accounts, SessionContext session)
    {
        _accounts = accounts;
        _session = session;
    }

    /// <summary>
    /// The account row. Never null: an account that somehow has no row reads as a fresh one
    /// carrying the signed-in address, which is what the row would have been seeded with anyway.
    /// </summary>
    public async Task<UserProfile> GetAsync() =>
        await _accounts.GetAsync() ?? new UserProfile
        {
            UserId = _session.UserId,
            Username = _session.Email,
        };

    public Task SaveAsync(UserProfile profile) => _accounts.UpsertAsync(profile);
}
