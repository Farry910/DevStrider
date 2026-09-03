using DevStrider.Desktop.Services.HrApi;

namespace DevStrider.Desktop.Services;

/// <summary>Outcome of a sign-in attempt. <paramref name="Message"/> is shown as-is.</summary>
public readonly record struct SignInResult(bool Ok, string Message, long UserId)
{
    public static SignInResult Fail(string message) => new(false, message, 0);
    public static SignInResult Success(long userId) => new(true, "", userId);
}

/// <summary>
/// Sign-in against hr-system, the company portal.
///
/// <para>
/// DevStrider does not own accounts and never has: it never inserts into <c>app_user</c>, never
/// sets a password, never verifies an address, and offers no sign-up or reset. What changed is how
/// it checks one — this used to open its own Postgres connection and re-implement the portal's
/// scrypt check in C#; now it is one HTTP call to <c>/api/devstrider/auth/login</c>
/// (<see cref="HrApiClient"/>), and hr-system is the only thing that ever compares a password.
/// </para>
///
/// <para>
/// An address with no account cannot sign in and cannot be created here: holding an hr-system
/// account is the only way to become a DevStrider user. The <c>ds_users</c> row that records who
/// someone is on the team is seeded by hr-system itself on every login, and its name is the
/// portal's address rather than anything typed here — one account, one identity, and no second
/// place for it to drift.
/// </para>
/// </summary>
public sealed class AuthService
{
    private readonly HrApiClient _api;
    private readonly SessionContext _session;
    private readonly ActivityLogService _activity;

    public AuthService(HrApiClient api, SessionContext session, ActivityLogService activity)
    {
        _api = api;
        _session = session;
        _activity = activity;
    }

    /// <summary>
    /// Verify the credentials against hr-system and, on success, install them in
    /// <see cref="SessionContext"/>. hr-system deliberately answers a wrong address and a wrong
    /// password with the same message — see its server.js — so this never distinguishes them either.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var typed = (email ?? "").Trim();
        if (typed.Length == 0) return SignInResult.Fail("Enter your email address.");
        if (string.IsNullOrEmpty(password)) return SignInResult.Fail("Enter your password.");

        try
        {
            var result = await _api.LoginAsync(typed, password, ct);
            _session.SignIn(result.UserId, result.Email);
            _activity.Success("Login", "Signed in", result.Email);
            return SignInResult.Success(result.UserId);
        }
        catch (HrApiException ex)
        {
            // hr-system already words these for a human: wrong credentials, unverified email,
            // missing fields. Shown as-is — see HrApiException.
            return SignInResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return SignInResult.Fail($"Couldn't reach hr-system. {ex.Message}");
        }
    }

    /// <summary>
    /// Restore a session from the bearer token saved on disk, with no password prompt. Returns
    /// false — never throws for the ordinary case — when there is nothing to restore; the caller
    /// falls back to the login window.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
            var identity = await _api.RestoreSessionAsync(ct);
            if (identity == null) return false;
            _session.SignIn(identity.UserId, identity.Email);
            _activity.Info("Login", "Session restored", identity.Email, silent: true);
            return true;
        }
        catch (Exception ex)
        {
            // A network hiccup on startup should land on the login form, not crash the app.
            System.Diagnostics.Debug.WriteLine($"[auth] session restore failed: {ex.Message}");
            return false;
        }
    }
}
