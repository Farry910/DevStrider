using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// Single-user profile for this local install. The role/title for each Experience entry lives in
/// the bid body's [Subtitle N] line (sourced from GPT output) rather than the profile.
/// </summary>
public class UserProfile
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Stable identity used as the GitHub-sync file prefix.</summary>
    public string Username { get; set; } = "me";
    public string DisplayName { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PersonalEmail { get; set; } = "";
    public string LinkedinUrl { get; set; } = "";

    public List<Education> Education { get; set; } = new();
    public List<Certification> Certifications { get; set; } = new();
    public List<Experience> Experiences { get; set; } = new();

    public Goals Goals { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Education
{
    public string Degree { get; set; } = "";
    public string School { get; set; } = "";
    public string Location { get; set; } = "";
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}

public class Certification
{
    public string Name { get; set; } = "";
    public string Issuer { get; set; } = "";
    public int? Year { get; set; }
}

public class Experience
{
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}

public class Goals
{
    public int BidsPerDay { get; set; }
    public int InterviewsPerWeek { get; set; }
    public int OffersPerMonth { get; set; }
}
