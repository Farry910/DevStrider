namespace DevStrider.Desktop.Services;

/// <summary>
/// Who is logged in. Set once — by a restored bearer token on a quiet startup, or by the login
/// window otherwise — before the main window is built.
///
/// <para>
/// The account id is no longer this object's business the way it used to be: every HTTP repository
/// in <c>Data/Http</c> reads it off the bearer token on hr-system's side
/// (<see cref="Services.HrApi.HrApiClient"/>), not from here, so there is no query left that could
/// accidentally run scoped to someone else. What lives here now is purely for display — the
/// title bar, the Settings tab's "signed in as" — and for the rest of the app to ask
/// <see cref="IsAuthenticated"/> before assuming a session exists.
/// </para>
/// </summary>
public sealed class SessionContext
{
    /// <summary><c>app_user.id</c>. Zero until <see cref="SignIn"/>.</summary>
    public long UserId { get; private set; }

    /// <summary>The signed-in portal address.</summary>
    public string Email { get; private set; } = "";

    public bool IsAuthenticated => UserId != 0;

    public void SignIn(long userId, string email)
    {
        if (userId == 0) throw new ArgumentOutOfRangeException(nameof(userId), "Not a real app_user id.");
        UserId = userId;
        Email = email ?? "";
    }
}
