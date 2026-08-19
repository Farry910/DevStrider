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
/// records who someone is on the team is seeded from that account on first successful login, and
/// its name is the portal's address rather than anything typed here — one account, one identity,
/// and no second place for it to drift.
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

                // Widening reads, not exact ones. app_user is the portal's table and its column
                // types are the portal's business: today `id` is integer and `email_verified` is
                // integer-as-flag, and a strict r.GetInt64/r.GetBoolean throws InvalidCastException
                // on those — which surfaced as "couldn't reach the database" and made every sign-in
                // fail for a reason nobody could act on. Read the value, then coerce.
                id = Convert.ToInt64(r.GetValue(0));
                // The address as the portal stores it, not as it was typed. Case and spacing are
                // whatever the account says, and that is what ds_users is keyed by below.
                address = r.IsDBNull(1) ? typed : r.GetString(1);
                hash = r.IsDBNull(2) ? "" : r.GetString(2);
                verified = ReadFlag(r, 3);
            }

            // An account with no usable hash can only have been created by something that doesn't
            // set passwords. It is not a wrong password, but it is not a way in either.
            if (hash.Length == 0) return SignInResult.Fail(wrong);
            if (!VerifyPortalPassword(password, hash)) return SignInResult.Fail(wrong);

            // Checked after the password on purpose: answering this before verifying would tell an
            // unauthenticated caller which addresses have accounts.
            if (!verified)
                return SignInResult.Fail(
                    "This account's email address hasn't been verified yet. Confirm it in the portal, then sign in here.");

            // Its own catch, not the outer one. Both this and the SELECT above can raise 42P01, but
            // they mean opposite things: no app_user is the wrong database entirely, while no
            // ds_users is the right database with the schema not yet applied. Letting the outer
            // handler answer for both told people to fix a connection that was already correct.
            try
            {
                await EnsureDsUserAsync(conn, id, address, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                return SignInResult.Fail(
                    "Your account is fine, but DevStrider's tables aren't in this database yet. "
                    + "Run desktop/shared-db-schema.sql against it once — for the whole team, not "
                    + "per machine — then sign in again.");
            }

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
    /// A truthy flag from a column whose type this app does not control. The portal stores
    /// <c>email_verified</c> as an integer; a different deployment could just as easily use a
    /// boolean or a text 't'. Anything unrecognised reads as not-verified, which fails closed.
    /// </summary>
    private static bool ReadFlag(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return false;
        return r.GetValue(ordinal) switch
        {
            bool b => b,
            short n => n != 0,
            int n => n != 0,
            long n => n != 0,
            decimal n => n != 0,
            string t => t is "1" or "t" or "T" or "true" or "True" or "TRUE" or "y" or "yes",
            _ => false,
        };
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
    /// There is nothing else on the row to write. Goals and achievement counters used to live here
    /// and are gone — nothing read them.
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

    // ── password verification ───────────────────────────────────────────────
    //
    // The portal stores `<saltHex>:<keyHex>` — a 16-byte salt and a 64-byte derived key, both
    // lower-case hex, 161 characters in total. That is the shape produced by Node's
    //
    //     const salt = crypto.randomBytes(16).toString('hex');
    //     const hash = crypto.scryptSync(password, salt, 64).toString('hex');
    //
    // so scrypt at Node's default parameters is what this reproduces. It is emphatically NOT
    // bcrypt, whatever earlier versions of this file claimed: no stored value starts with $2.

    /// <summary>Node's <c>crypto.scrypt</c> defaults. Changing these invalidates every login.</summary>
    private const int ScryptN = 16384;   // cost
    private const int ScryptR = 8;       // block size
    private const int ScryptP = 1;       // parallelisation
    private const int ScryptKeyLength = 64;

    /// <summary>
    /// Verify a password against the portal's stored value. Any malformed or unrecognised stored
    /// value is a failed sign-in, never an exception — a crashed login window tells the user
    /// nothing and loses the rest of the session with it.
    /// </summary>
    private static bool VerifyPortalPassword(string password, string stored)
    {
        var sep = stored.IndexOf(':');
        if (sep <= 0 || sep >= stored.Length - 1) return false;

        if (!TryParseHex(stored.AsSpan(sep + 1), out var expected)) return false;
        if (expected.Length != ScryptKeyLength) return false;

        var saltText = stored[..sep];

        // Two readings of "the salt", tried in order.
        //
        // Node's snippet above passes the *hex string* to scryptSync, which encodes it as UTF-8 —
        // so the salt fed to the KDF is 32 ASCII bytes, not the 16 bytes they spell. A portal that
        // decoded the hex first would be equally reasonable and is indistinguishable from the
        // stored value alone. Trying both costs one extra derivation on a failed login and nothing
        // on a successful one, and it is not a security compromise: a wrong reading simply does not
        // match. Collapse this to whichever branch wins once the portal's source is confirmed —
        // the Debug line below names it.
        if (Matches(password, System.Text.Encoding.UTF8.GetBytes(saltText), expected))
        {
            System.Diagnostics.Debug.WriteLine("[auth] scrypt matched with UTF-8 hex-string salt (Node default)");
            return true;
        }
        if (TryParseHex(saltText.AsSpan(), out var saltBytes) && Matches(password, saltBytes, expected))
        {
            System.Diagnostics.Debug.WriteLine("[auth] scrypt matched with decoded 16-byte salt");
            return true;
        }
        return false;
    }

    private static bool Matches(string password, byte[] salt, byte[] expected)
    {
        try
        {
            var actual = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
                System.Text.Encoding.UTF8.GetBytes(password), salt,
                ScryptN, ScryptR, ScryptP, ScryptKeyLength);
            // Fixed-time compare: a length-or-first-difference exit leaks how much of the key was
            // right, one byte at a time.
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[auth] scrypt derivation failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex.Length == 0 || hex.Length % 2 != 0) return false;
        var buffer = new byte[hex.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!byte.TryParse(hex.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out buffer[i]))
                return false;
        }
        bytes = buffer;
        return true;
    }
}
