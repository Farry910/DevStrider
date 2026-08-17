namespace DevStrider.Desktop.Services;

/// <summary>
/// Who is logged in. Set once by the login window before the main window is built, and read by
/// every repository to scope its queries.
///
/// <para>
/// There is no persisted session by design: the password is asked for on every start of the app.
/// This object holds the answer for the lifetime of the process and nothing writes it to disk.
/// </para>
///
/// <para>
/// In one database holding the whole team, "my rows" is a predicate rather than a given, and
/// <see cref="UserId"/> is that predicate. Repositories read it from here rather than taking it as
/// a parameter — a caller that could pass a user id is a caller that could pass the wrong one.
/// </para>
/// </summary>
public sealed class SessionContext
{
    /// <summary><c>app_user.id</c>. Zero until <see cref="SignIn"/> — see <see cref="Require"/>.</summary>
    public long UserId { get; private set; }

    /// <summary>The address that was typed into the login form, as <c>app_user</c> holds it.</summary>
    public string Email { get; private set; } = "";

    public bool IsAuthenticated => UserId != 0;

    public void SignIn(long userId, string email)
    {
        if (userId == 0) throw new ArgumentOutOfRangeException(nameof(userId), "Not a real app_user id.");
        UserId = userId;
        Email = email ?? "";
    }

    /// <summary>
    /// The account id, or a loud failure. Every repository call goes through this: a query that
    /// silently ran with user_id = 0 would return an empty board rather than an error, and the
    /// user would think their data was gone.
    /// </summary>
    public long Require() =>
        IsAuthenticated
            ? UserId
            : throw new InvalidOperationException("No user is signed in — the database was reached before login.");
}
