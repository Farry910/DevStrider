using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

public static class BidStatuses
{
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

public class UserBid
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId GroupLinkId { get; set; }

    public string ResumeId { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> PrimaryStacks { get; set; } = new();

    public string Status { get; set; } = BidStatuses.Draft;
    public string Origin { get; set; } = "LinkedIn";

    public string JobDescription { get; set; } = "";
    public string GptResumeContent { get; set; } = "";
    public string Comment { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Immutable row-creation timestamp; never moves on edits.</summary>
    public DateTime FirstCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// First moment the bid moved off `draft`. Stays null while the row is still an "empty" draft;
    /// set once and locked. Used by anything that needs to count "real" bids by apply time rather
    /// than row-creation time (e.g. the bids-per-10-min chart).
    /// </summary>
    public DateTime? AppliedAt { get; set; }
}
