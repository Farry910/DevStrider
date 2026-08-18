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
///
/// <para>
/// An address with no <c>app_user</c> row cannot sign in and cannot be created here: holding a
/// portal account is the only way to become a DevStrider user. The <c>ds_users</c> row that
/// carries goals and achievements is seeded from that account on first successful login, and its
/// name is the portal's address rather than anything typed here — one account, one identity, and
/// no second place for it to drift.
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
        var typed = (email ?? "").Trim();
        if (typed.Length == 0) return SignInResult.Fail("Enter your email address.");
        if (string.IsNullOrEmpty(password)) return SignInResult.Fail("Enter your password.");

        if (!await _db.IsConfiguredAsync())
            return SignInResult.Fail("The database isn't configured yet — fill in the connection details first.");

        const string wrong = "That email and password don't match an account.";

        try
        {
            await using var conn = await _db.OpenAsync(ct);

            long id;
            string address;
            string hash;
            bool verified;

            await using (var cmd = new NpgsqlCommand(
                "SELECT id, email, password_hash, email_verified FROM app_user WHERE lower(email) = lower(@e)", conn))
            {
                cmd.Parameters.AddWithValue("e", typed);
                await using var r = await cmd.ExecuteReaderAsync(ct);

                // No app_user row is the end of it. There is no sign-up here, and no way to become
                // a DevStrider user without being a portal user first.
                if (!await r.ReadAsync(ct)) return SignInResult.Fail(wrong);

                id = r.GetInt64(0);
                // The address as the portal stores it, not as it was typed. Case and spacing are
                // whatever the account says, and that is what ds_users is keyed by below.
                address = r.IsDBNull(1) ? typed : r.GetString(1);
                hash = r.IsDBNull(2) ? "" : r.GetString(2);
                verified = !r.IsDBNull(3) && r.GetBoolean(3);
            }

            // An account with no usable hash can only have been created by something that doesn't
            // set passwords. It is not a wrong password, but it is not a way in either.
            if (hash.Length == 0) return SignInResult.Fail(wrong);
            if (!VerifyBcrypt(password, hash)) return SignInResult.Fail(wrong);

            // Checked after the password on purpose: answering this before verifying would tell an
            // unauthenticated caller which addresses have accounts.
            if (!verified)
                return SignInResult.Fail(
                    "This account's email address hasn't been verified yet. Confirm it in the portal, then sign in here.");

            await EnsureDsUserAsync(conn, id, address, ct);

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
    /// Create or refresh the <c>ds_users</c> row behind the account that just signed in.
    ///
    /// <para>
    /// <c>ds_users.username</c> is the portal address. It could have been a name of its own, but
    /// then two things would claim to say who someone is, and they would disagree the first time
    /// one of them changed. Every login re-asserts it, so a rename in the portal arrives here
    /// rather than leaving a stale label on the Peers tab for ever.
    /// </para>
    ///
    /// <para>
    /// The goal columns are left to their defaults on insert and never touched here. They are the
    /// user's own targets; re-asserting them on each login would reset them on every launch.
    /// </para>
    /// </summary>
    private static async Task EnsureDsUserAsync(
        NpgsqlConnection conn, long userId, string email, CancellationToken ct)
    {
        const string sql =
            "INSERT INTO ds_users (user_id, username, created_at, updated_at) " +
            "VALUES (@uid, @un, now(), now()) " +
            "ON CONFLICT (user_id) DO UPDATE SET username = EXCLUDED.username, updated_at = now()";
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("un", email);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // username is UNIQUE and somebody else already holds this address — only reachable if
            // the portal moved an address between two accounts. The existing row keeps its name
            // and the login proceeds: nothing downstream reads the name to decide what a user may
            // see, so a stale label is a cosmetic problem and a blocked login would not be.
            System.Diagnostics.Debug.WriteLine($"ds_users username collision for {userId}: {ex.MessageText}");
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
