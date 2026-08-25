namespace DevStrider.Desktop.Services;

/// <summary>Outcome of a sign-in attempt. <paramref name="Message"/> is shown as-is.</summary>
public readonly record struct SignInResult(bool Ok, string Message, long UserId)
{
    public static SignInResult Fail(string message) => new(false, message, 0);
    public static SignInResult Success(long userId) => new(true, "", userId);
}

/// <summary>What <c>/api/devstrider/auth/login</c> and <c>/refresh</c> answer with.</summary>
public sealed class LoginResponse
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime IssuedAt { get; set; }
    public int ExpiresInSeconds { get; set; }
    public long UserId { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>
/// Sign-in, against the company portal's HTTP API.
///
/// <para>
/// <b>This app does not check passwords.</b> It used to: it read <c>app_user.password_hash</c>
/// straight out of the database and re-derived the portal's scrypt in C#, complete with a guess at
/// which of two readings of the salt the portal had meant. That was a second implementation of an
/// authentication rule, in a different language, on every laptop, with the database credential
/// beside it — and nothing that would have told anyone if the portal changed the rule. The
/// password now goes to the portal over TLS and the portal answers with a token.
/// </para>
///
/// <para>
/// DevStrider still does not own accounts. It never creates one, never sets a password, and offers
/// no sign-up or reset: an address with no portal account cannot sign in here, which is the same
/// contract as before and is now enforced in the one place that can enforce it.
/// </para>
///
/// <para>
/// The token is good for a week and is kept across restarts (<see cref="SessionStore"/>), so
/// signing in is something that happens on Monday rather than every time the app opens.
/// </para>
/// </summary>
public sealed class AuthService
{
    private readonly PortalApi _api;
    private readonly SessionContext _session;
    private readonly SessionStore _store;
    private readonly ActivityLogService _activity;

    public AuthService(PortalApi api, SessionContext session, SessionStore store, ActivityLogService activity)
    {
        _api = api;
        _session = session;
        _store = store;
        _activity = activity;
    }

    /// <summary>
    /// Exchange a password for a week-long token and install it.
    ///
    /// <para>
    /// A wrong address and a wrong password produce the same message, and they do so because the
    /// portal answers both the same way — this app could not distinguish them if it wanted to,
    /// which is the improvement over deciding not to.
    /// </para>
    /// </summary>
    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var typed = (email ?? "").Trim();
        if (typed.Length == 0) return SignInResult.Fail("Enter your email address.");
        if (string.IsNullOrEmpty(password)) return SignInResult.Fail("Enter your password.");

        if (!await _api.IsConfiguredAsync())
            return SignInResult.Fail("The portal address isn't set yet — fill it in first.");

        try
        {
            var login = await _api.PostAsync<LoginResponse>("/api/devstrider/auth/login",
                new { email = typed, password }, ct);
            if (login == null || string.IsNullOrEmpty(login.Token))
                return SignInResult.Fail("The portal accepted the sign-in but sent no token back.");

            Install(login);
            _activity.Success("Login", "Signed in", $"{login.Email} · session valid until {login.ExpiresAt.ToLocalTime():g}");
            return SignInResult.Success(login.UserId);
        }
        catch (PortalApiException ex)
        {
            return SignInResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return SignInResult.Fail($"Couldn't reach the portal. {Safe.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// Put a saved session back, if there is one that is still in date and that the portal still
    /// honours. This is what makes the week mean anything: the app opens straight onto the bid
    /// board rather than onto a password box.
    ///
    /// <para>
    /// The stored token is checked against the portal rather than trusted on its expiry alone.
    /// Nothing else would notice an account that has since been closed, or a signing key that was
    /// rotated to revoke every outstanding token — both would otherwise surface as the first
    /// ordinary action of the day failing with a 401.
    /// </para>
    ///
    /// <para>
    /// A portal that cannot be reached is <b>not</b> a rejected session. The laptop is on a train;
    /// the token is still good. The session is installed and the first real call will say so if it
    /// isn't — throwing the token away here would mean needing a network to find out you still had
    /// a valid one.
    /// </para>
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken ct = default)
    {
        var saved = _store.Load();
        if (saved == null) return false;

        _session.SignIn(saved);

        try
        {
            var identity = await _api.GetAsync<LoginResponse>("/api/devstrider/auth/session", ct);
            if (identity != null && identity.UserId != 0)
            {
                // Names and roles change in the portal; the token does not carry them. Take the
                // fresh copy and keep the token and its expiry.
                saved.Email = identity.Email;
                saved.Username = identity.Username;
                saved.Name = identity.Name;
                saved.Role = identity.Role;
                _session.SignIn(saved);
                _store.Save(saved);
            }

            if (_session.NeedsRefresh) await RefreshAsync(ct);
            return true;
        }
        catch (PortalApiException ex) when (ex.IsUnauthorized)
        {
            SignOut();
            return false;
        }
        catch (PortalApiException ex)
        {
            _activity.Warning("Login", "Signed in from the saved session without reaching the portal", ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Trade the current token for a fresh week. Called at startup when the saved one is inside
    /// its last day, so somebody who opens DevStrider on any given weekday is never asked for a
    /// password again.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (!_session.IsAuthenticated) return false;
        try
        {
            var login = await _api.PostAsync<LoginResponse>("/api/devstrider/auth/refresh", null, ct);
            if (login == null || string.IsNullOrEmpty(login.Token)) return false;
            Install(login);
            _activity.Info("Login", "Session extended", $"Valid until {login.ExpiresAt.ToLocalTime():g}");
            return true;
        }
        catch (PortalApiException ex) when (ex.IsUnauthorized)
        {
            SignOut();
            return false;
        }
        catch (PortalApiException)
        {
            // Offline. The current token is still valid until it isn't; try again next launch.
            return false;
        }
    }

    /// <summary>
    /// Drop the session, here and on disk.
    ///
    /// <para>
    /// The portal is told as a courtesy and the answer is not waited on: these tokens are
    /// stateless, so there is no server-side row to delete and nothing about the outcome changes
    /// what happens on this machine. What actually ends the session is the file going away.
    /// </para>
    /// </summary>
    public void SignOut()
    {
        if (_session.IsAuthenticated)
            _ = _api.PostAsync<object>("/api/devstrider/auth/logout", null).ContinueWith(
                t => System.Diagnostics.Debug.WriteLine($"logout: {t.Exception?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

        _store.Clear();
        _session.SignOut();
        _activity.Info("Login", "Signed out", "The saved session was removed from this machine.");
    }

    private void Install(LoginResponse login)
    {
        var session = new PortalSession
        {
            Token = login.Token,
            // A server that sent no expiry would otherwise install a session that never ends. Fall
            // back to the week the portal promises rather than to DateTime.MinValue, which would
            // read as already expired and loop the user back to the login window.
            ExpiresAt = login.ExpiresAt == default ? DateTime.UtcNow.AddDays(7) : login.ExpiresAt,
            IssuedAt = login.IssuedAt == default ? DateTime.UtcNow : login.IssuedAt,
            UserId = login.UserId,
            Email = login.Email,
            Username = string.IsNullOrEmpty(login.Username) ? login.Email : login.Username,
            Name = login.Name,
            Role = login.Role,
        };
        _session.SignIn(session);
        _store.Save(session);
    }
}
