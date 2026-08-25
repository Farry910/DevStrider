namespace DevStrider.Desktop.Services;

/// <summary>
/// What the portal handed back when this machine signed in: who you are, and the bearer token
/// that proves it for the next week.
/// </summary>
public sealed class PortalSession
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime IssuedAt { get; set; }
    public long UserId { get; set; }
    public string Email { get; set; } = "";

    /// <summary>The <c>ds_users</c> name, which is the portal address — see <see cref="SessionContext"/>.</summary>
    public string Username { get; set; } = "";

    /// <summary>The person's name from the portal, when their account is attached to one. Display only.</summary>
    public string Name { get; set; } = "";

    public string Role { get; set; } = "";
}

/// <summary>
/// Who is signed in, and what proves it.
///
/// <para>
/// This used to be two fields and no secret: the app verified the password itself against
/// <c>app_user.password_hash</c>, kept the resulting user id in memory, and asked for the password
/// again on every start — because the only alternative on offer was writing a password to disk.
/// Authentication happens at the portal now and what comes back is a token with an expiry on it,
/// which is a thing that <i>can</i> be kept: it is scoped to DevStrider, it dies on its own after
/// a week, and the portal can decline it at any point before that.
/// </para>
///
/// <para>
/// So there is a persisted session now, and it is the point of the change: sign in on Monday and
/// the app opens straight onto the bid board every day after. See <see cref="SessionStore"/> for
/// where the token sits and what protects it.
/// </para>
///
/// <para>
/// <see cref="UserId"/> stays what every repository scopes its reads to, and the server pins every
/// write to the same id off the token — so a request cannot ask for someone else's rows even if
/// this app were made to try.
/// </para>
/// </summary>
public sealed class SessionContext
{
    /// <summary>Inside this much of the expiry, the app trades the token for a fresh week.</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(1);

    private PortalSession? _current;

    /// <summary>Raised whenever the session is installed, refreshed or dropped. Fires off the UI thread.</summary>
    public event Action? Changed;

    /// <summary><c>app_user.id</c>. Zero until <see cref="SignIn"/> — see <see cref="Require"/>.</summary>
    public long UserId => _current?.UserId ?? 0;

    /// <summary>The portal address the account is held under, as the portal spells it.</summary>
    public string Email => _current?.Email ?? "";

    public string Username => _current?.Username ?? "";
    public string Name => _current?.Name ?? "";
    public string Role => _current?.Role ?? "";

    /// <summary>The bearer token, or empty. Read on every request by <see cref="PortalApi"/>.</summary>
    public string Token => _current?.Token ?? "";

    public DateTime ExpiresAt => _current?.ExpiresAt ?? DateTime.MinValue;

    /// <summary>
    /// A usable session. The expiry is checked here rather than left to the portal so a token
    /// known to be dead is never sent — the answer would be a 401, and a 401 at startup reads to
    /// the user as a broken server rather than as a week having passed.
    /// </summary>
    public bool IsAuthenticated => _current != null && _current.UserId != 0
        && !string.IsNullOrEmpty(_current.Token) && _current.ExpiresAt > DateTime.UtcNow;

    /// <summary>Inside the last day of the week — time to ask for a new token.</summary>
    public bool NeedsRefresh => IsAuthenticated && _current!.ExpiresAt - DateTime.UtcNow < RefreshWindow;

    /// <summary>How long this session has left. Shown on the About tab.</summary>
    public TimeSpan Remaining => IsAuthenticated ? _current!.ExpiresAt - DateTime.UtcNow : TimeSpan.Zero;

    public PortalSession? Current => _current;

    public void SignIn(PortalSession session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (session.UserId == 0) throw new ArgumentOutOfRangeException(nameof(session), "Not a real app_user id.");
        if (string.IsNullOrEmpty(session.Token)) throw new ArgumentException("No token on the session.", nameof(session));
        _current = session;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        _current = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// The account id, or a loud failure. Every repository call goes through this: a request that
    /// silently ran with user_id = 0 would come back as an empty board rather than as an error,
    /// and the user would think their work was gone.
    /// </summary>
    public long Require() =>
        IsAuthenticated
            ? _current!.UserId
            : throw new InvalidOperationException("No user is signed in — the portal was called before login.");
}
