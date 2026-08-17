using Npgsql;

namespace DevStrider.Desktop.Services;

/// <summary>Outcome of a sign-in attempt. <paramref name="Message"/> is shown as-is.</summary>
public readonly record struct SignInResult(bool Ok, string Message, long UserId)
{
    public static SignInResult Fail(string message) => new(false, message, 0);
    public static SignInResult Success(long userId) => new(true, "", userId);
}

/// <summary>
/// Sign-in against the company portal's <c>app_user</c> table.
///
/// <para>
/// DevStrider does not own accounts. It never inserts into <c>app_user</c>, never sets a password,
/// never verifies an address, and offers no sign-up or reset — all of that is the portal's, and
/// duplicating any of it here would mean two apps disagreeing about who someone is. This class
/// reads one row and checks one hash.
/// </para>
/// </summary>
public sealed class AuthService
{
    private readonly SharedDbContext _db;
    private readonly SessionContext _session;
    private readonly ActivityLogService _activity;

    public AuthService(SharedDbContext db, SessionContext session, ActivityLogService activity)
    {
        _db = db;
        _session = session;
        _activity = activity;
    }

    /// <summary>
    /// Verify the credentials and, on success, install them in <see cref="SessionContext"/>.
    ///
    /// <para>
    /// A wrong address and a wrong password deliberately produce the same message. The portal is
    /// where accounts are managed; a login form that distinguished "no such user" would report on
    /// who has an account there to anyone holding the database credential.
    /// </para>
    /// </summary>
    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var address = (email ?? "").Trim();
        if (address.Length == 0) return SignInResult.Fail("Enter your email address.");
        if (string.IsNullOrEmpty(password)) return SignInResult.Fail("Enter your password.");

        if (!await _db.IsConfiguredAsync())
            return SignInResult.Fail("The database isn't configured yet — fill in the connection details first.");

        const string wrong = "That email and password don't match an account.";

        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT id, password_hash, email_verified FROM app_user WHERE lower(email) = lower(@e)", conn);
            cmd.Parameters.AddWithValue("e", address);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return SignInResult.Fail(wrong);

            var id = r.GetInt64(0);
            var hash = r.IsDBNull(1) ? "" : r.GetString(1);
            var verified = !r.IsDBNull(2) && r.GetBoolean(2);

            // An account with no usable hash can only have been created by something that doesn't
            // set passwords. It is not a wrong password, but it is not a way in either.
            if (hash.Length == 0) return SignInResult.Fail(wrong);
            if (!VerifyBcrypt(password, hash)) return SignInResult.Fail(wrong);

            // Checked after the password on purpose: answering this before verifying would tell an
            // unauthenticated caller which addresses have accounts.
            if (!verified)
                return SignInResult.Fail(
                    "This account's email address hasn't been verified yet. Confirm it in the portal, then sign in here.");

            _session.SignIn(id, address);
            _activity.Success("Login", "Signed in", address);
            return SignInResult.Success(id);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return SignInResult.Fail(
                "The database is reachable but has no app_user table — this connection points somewhere "
                + "other than the company portal's database.");
        }
        catch (PostgresException ex)
        {
            return SignInResult.Fail($"The database rejected the query: {ex.MessageText} (SQLSTATE {ex.SqlState})");
        }
        catch (Exception ex)
        {
            return SignInResult.Fail($"Couldn't reach the database. {SharedDbCredentials.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// The portal hashes with bcrypt. A stored value in any other format — a legacy scheme, a
    /// truncated column, a placeholder — throws rather than comparing, and that has to read as a
    /// failed sign-in and not as a crashed app.
    /// </summary>
    private static bool VerifyBcrypt(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
