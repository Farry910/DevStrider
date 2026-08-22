
namespace DevStrider.Desktop.Models;

/// <summary>
/// One bidding identity as seen by the rest of the team: a (person, profile) pair, not a person.
/// Someone with three profiles has three of these; group by <see cref="UserId"/> to get theirs.
///
/// <para>
/// This is a read projection, not a stored row. It used to be one — <c>peer_users</c>, mirrored
/// into every machine's local database — back when each install kept its real data privately and
/// pushed summaries up. With one shared database there is nothing to mirror: a teammate's bids
/// are simply <see cref="UserBid"/> rows with a different <see cref="UserBid.UserId"/>, and this
/// is the join that puts a name on them.
/// </para>
///
/// <para>
/// <b>Identification, not authentication.</b> Who wrote a row is whoever was logged in; nothing
/// downstream should treat these fields as proof of authorship.
/// </para>
/// </summary>
public class PeerIdentity
{
    /// <summary><c>app_user.id</c> — the person.</summary>
    public long UserId { get; set; }

    /// <summary>Their DevStrider user name. Display text; safe to rename.</summary>
    public string Username { get; set; } = "";

    public ObjectId ProfileId { get; set; }

    /// <summary>Display name of the profile. Safe to rename; nothing copies it.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>
    /// Stable key for the profile. Unique only in combination with <see cref="UserId"/> — two
    /// people can both have a profile slugged "default", which is why rows reference the id.
    /// </summary>
    public string ProfileSlug { get; set; } = "";

    /// <summary>Contact address from the profile's CV. Informational.</summary>
    public string Email { get; set; } = "";

    /// <summary>"alice · Default" — what the Peers picker shows.</summary>
    public string Display =>
        string.IsNullOrWhiteSpace(ProfileName) ? Username : $"{Username} · {ProfileName}";
}
