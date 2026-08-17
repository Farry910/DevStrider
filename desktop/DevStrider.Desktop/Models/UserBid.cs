using MongoDB.Bson;

namespace DevStrider.Desktop.Models;

public static class BidStatuses
{
    /// <summary>
    /// The URL has been captured but nothing has been bid yet. This is what a bare job link
    /// <i>is</i> — see the note on <see cref="UserBid"/> about the merge.
    /// </summary>
    public const string Draft = "draft";

    public const string Applied = "applied";
    public const string Screening = "screening";
    public const string PhoneScreening = "phone_screening";
    public const string Interview = "interview";
    public const string Offer = "offer";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Accepted = "accepted";

    public static readonly string[] All =
    {
        Draft, Applied, Screening, PhoneScreening, Interview,
        Offer, Rejected, Withdrawn, Accepted
    };
}

/// <summary>
/// A job posting and the bid made against it — one row, one thing.
///
/// <para>
/// These used to be two: a <c>GroupLink</c> holding the URL, and a bid pointing at it. The
/// relationship was always one-to-one, and a link with no bid behind it is exactly what
/// <see cref="BidStatuses.Draft"/> already means. So the row is created when the URL is captured
/// and filled in when the bid is actually made.
/// </para>
///
/// <para>
/// Three columns died in that merge and are not coming back: the link's applied-company /
/// applied-role / applied-stacks snapshot duplicated <see cref="Company"/> / <see cref="Role"/> /
/// <see cref="PrimaryStacks"/> with a fallback between them, and its shared job description was a
/// second copy of <see cref="JobDescription"/> that the JD viewer already fell back to.
/// </para>
/// </summary>
public class UserBid
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Owning account — <c>app_user.id</c>. Stamped by the repository on write.</summary>
    public long UserId { get; set; }

    /// <summary>Owning profile. <see cref="ObjectId.Empty"/> until a profile is stamped on.</summary>
    public ObjectId ProfileId { get; set; }

    // ── the posting ─────────────────────────────────────────────────────────

    public string Url { get; set; } = "";

    /// <summary>
    /// Canonical form for dedup: lower-cased href with trailing slash trimmed; query + hash
    /// preserved. Different query strings are different postings, deliberately.
    /// </summary>
    public string UrlNorm { get; set; } = "";

    /// <summary>
    /// Set when the posting is written off as not worth bidding on. Distinct from simply having
    /// no bid yet, which is <see cref="BidStatuses.Draft"/>.
    /// </summary>
    public DateTime? MarkedUselessAt { get; set; }

    // ── the bid ─────────────────────────────────────────────────────────────

    public string ResumeId { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> PrimaryStacks { get; set; } = new();

    public string Status { get; set; } = BidStatuses.Draft;
    public string Origin { get; set; } = "LinkedIn";

    public string JobDescription { get; set; } = "";
    public string GptResumeContent { get; set; } = "";
    public string Comment { get; set; } = "";

    /// <summary>When the URL was captured. Immutable — edits move <see cref="UpdatedAt"/>.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// First moment the row moved off <see cref="BidStatuses.Draft"/>. Set once and then locked.
    /// Anything counting real bids by when they were sent reads this rather than
    /// <see cref="CreatedAt"/>, which is only when the link was seen.
    /// </summary>
    public DateTime? AppliedAt { get; set; }
}
