using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One <b>bidding identity</b> in the shared database: a (person, profile) pair, not a person.
/// Someone with three profiles has three of these; group by <see cref="Username"/> to get their
/// profiles.
///
/// <para>
/// <see cref="RemoteId"/> is the shared database's generated key and the target of every
/// <c>owner_user_id</c> foreign key — so one column on a bid names the person <i>and</i> the
/// profile. The profile name is therefore stored exactly once. Copying it onto each bid was the
/// design flaw this replaces: rename a profile and every historical bid keeps the old string,
/// silently splitting one identity's history in two.
/// </para>
///
/// <para>
/// <b>Identification, not authentication.</b> There is no password anywhere in this system — any
/// client holding the shared database credential can write a row claiming any username. That is
/// acceptable for a small trusted team, but nothing downstream should treat
/// <see cref="Username"/> as proof of who wrote a row.
/// </para>
/// </summary>
public class PeerUser
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>The shared database's generated <c>peer_users.id</c>. 0 until first synced.</summary>
    public long RemoteId { get; set; }

    /// <summary>Stable lowercase handle for the person. Repeats across their profiles.</summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Stable key for the profile, from <see cref="Profile.Slug"/>. Unique only in combination
    /// with <see cref="Username"/> — two people can both have a profile slugged "default", which
    /// is exactly why a bid references the row id rather than this text.
    /// </summary>
    public string ProfileSlug { get; set; } = "";

    /// <summary>Display name of the profile. Safe to rename; nothing copies it.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>Contact address. Informational — never used to authenticate.</summary>
    public string Email { get; set; } = "";

    /// <summary>When this identity first appeared in the shared database. Never rewritten.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
