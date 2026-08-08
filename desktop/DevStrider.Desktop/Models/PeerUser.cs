using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One DevStrider install's identity as published to the shared cluster, plus the bidding
/// profiles it owns. This is what makes teammates <em>discoverable</em>: without it a peer only
/// appears once they've pushed a bid, so a colleague who set up today but hasn't bid yet would be
/// invisible.
///
/// <para>
/// Keyed by <see cref="Username"/> rather than an ObjectId, because the same person reinstalling
/// should land on their existing row instead of creating a second identity.
/// </para>
///
/// <para>
/// <b>Identification, not authentication.</b> There is no password anywhere in this system — any
/// client holding the shared cluster credential can write a row claiming any username. That is
/// acceptable for a small trusted team, but nothing downstream should treat
/// <see cref="Username"/> as proof of who wrote a row.
/// </para>
/// </summary>
public class PeerUser
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Stable lowercase handle. Matches <c>OwnerUsername</c> on every pushed bid/interview.</summary>
    public string Username { get; set; } = "";

    /// <summary>Contact address. Informational — never used to authenticate.</summary>
    public string Email { get; set; } = "";

    /// <summary>Human name for display, falling back to the username when unset.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The bidding identities this user owns, so the Peers tab can offer them.</summary>
    public List<PeerUserProfile> Profiles { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One bidding profile belonging to a <see cref="PeerUser"/>. Deliberately thin — the local
/// <see cref="Profile"/> also carries a Word document path and a ChatGPT prompt, and neither is
/// any of a peer's business.
/// </summary>
public class PeerUserProfile
{
    /// <summary>Matches <c>OwnerProfileSlug</c> on pushed rows — the join key.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Matches <c>OwnerProfileName</c>. What the Peers tab shows in its picker.</summary>
    public string Name { get; set; } = "";
}
