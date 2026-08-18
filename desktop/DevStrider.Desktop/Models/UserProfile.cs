namespace DevStrider.Desktop.Models;

/// <summary>
/// The DevStrider account — one row per <c>app_user</c> that has ever logged in, created on first
/// successful login.
///
/// <para>
/// The company portal owns the account itself: email, password, verification, role. This is only
/// what DevStrider knows about it, and it is deliberately almost nothing. Everything describing a
/// <i>person being bid for</i> — name, contact details, which Word template — is on
/// <see cref="Profile"/>, because one account runs several of those.
/// </para>
/// </summary>
public class UserProfile
{
    /// <summary><c>app_user.id</c>. The identity every owned row carries.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// The DevStrider user name, and it <i>is</i> the portal address on <c>app_user.email</c>.
    /// Login re-asserts it on every sign-in, so a rename in the portal follows the user here and
    /// there is never a second answer to who someone is.
    /// </summary>
    public string Username { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
