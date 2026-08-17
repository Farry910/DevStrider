namespace DevStrider.Desktop.Models;

/// <summary>
/// The DevStrider account — one row per <c>app_user</c> that has ever logged in, created on first
/// successful login.
///
/// <para>
/// The company portal owns the account itself: email, password, verification, role. This is only
/// what DevStrider knows about it, and it is deliberately thin. Everything describing a
/// <i>person being bid for</i> — name, CV, contact details — is on <see cref="Profile"/>, because
/// one account runs several of those.
/// </para>
/// </summary>
public class UserProfile
{
    /// <summary><c>app_user.id</c>. The identity every owned row carries.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// The DevStrider user name — one per account. Lower-cased with spaces as dashes; unique
    /// across the team. Display only: nothing references it, so renaming is safe.
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Targets are per person, not per bidding identity: someone running three profiles has one
    /// daily bid target, not three. The achievement counters have always worked this way.
    /// </summary>
    public Goals Goals { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Goals
{
    public int BidsPerDay { get; set; }
    public int InterviewsPerWeek { get; set; }
    public int OffersPerMonth { get; set; }
}
