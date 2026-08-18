using Npgsql;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Connection to the shared PostgreSQL database — the only store DevStrider has.
///
/// <para>
/// The database is shared with the company portal. DevStrider owns the eight <c>ds_*</c> tables
/// listed in <see cref="OwnedTables"/> and reads <c>app_user</c>, which belongs to the portal. It
/// touches nothing else, and it issues no DDL at all.
/// </para>
///
/// <para>
/// No connection is held open. Npgsql pools underneath, so opening per operation costs nothing
/// and avoids holding a slot on an instance that caps connections.
/// </para>
/// </summary>
public sealed class SharedDbContext
{
    /// <summary>
    /// The tables DevStrider owns, in the order <c>shared-db-schema.sql</c> defines them.
    /// <c>app_user</c> is deliberately absent: the portal owns it, this app only reads it, and its
    /// absence is a different failure — the wrong database entirely — which login reports itself.
    /// </summary>
    public static readonly string[] OwnedTables =
    {
        "ds_users", "ds_profiles", "ds_education", "ds_certifications",
        "ds_experiences", "ds_bids", "ds_interviews", "ds_achievements",
    };

    private readonly SharedDbCredentials _credentials;

    public SharedDbContext(SharedDbCredentials credentials)
    {
        _credentials = credentials;
    }

    public Task<bool> IsConfiguredAsync() => _credentials.IsConfiguredAsync();

    /// <summary>
    /// An open connection. Caller disposes.
    ///
    /// <para>
    /// This app issues no DDL. The schema is owned and applied by hand — see
    /// <c>desktop/shared-db-schema.sql</c>. A shared database can hold other applications'
    /// tables, and a client that quietly creates or alters things in it is a liability, not a
    /// convenience. If the tables are missing, queries fail with a clear PostgreSQL error rather
    /// than the app inventing a schema.
    /// </para>
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(await _credentials.BuildConnectionStringAsync());
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Reachability probe with a short timeout, so a wrong host fails in seconds rather than
    /// leaving the user staring at a spinner. Also reports any of the expected tables that are
    /// missing — since the app no longer creates them, that's the failure most likely to be
    /// waiting, and it's better found here than on the first query after login.
    /// </summary>
    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        if (!await _credentials.IsConfiguredAsync())
            return (false, "Shared database isn't configured — fill it in on the sign-in window, or in Settings.");

        try
        {
            await using var conn = await OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT current_database(), current_user, version()", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (false, "Connected, but the server returned nothing.");

            var db = r.GetString(0);
            var user = r.GetString(1);
            var version = r.GetString(2);
            // "PostgreSQL 16.2 on x86_64-pc-linux-gnu, compiled by ..." — first two words is plenty.
            var shortVersion = string.Join(' ', version.Split(' ').Take(2));

            var present = await ListTablesAsync();
            var missing = OwnedTables.Where(t => !present.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                return (false,
                    $"Connected to {db} as {user}, but these tables are missing: {string.Join(", ", missing)}."
                    + Environment.NewLine
                    + "Run desktop/shared-db-schema.sql against this database first — DevStrider does not create them.");
            }

            return (true, $"Connected to {db} as {user} · {shortVersion} · all tables present");
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        {
            return (false,
                "Timed out reaching the server. Common causes:\n" +
                "  • Host or port wrong\n" +
                "  • The provider's firewall doesn't allow this machine's IP\n" +
                "  • SSL required but switched off here\n" +
                $"Underlying: {SharedDbCredentials.Redact(ex.Message)}");
        }
        catch (PostgresException ex)
        {
            // The server answered and refused — its own message is the useful one.
            return (false, $"PostgreSQL rejected the connection: {ex.MessageText} (SQLSTATE {ex.SqlState})");
        }
        catch (Exception ex)
        {
            return (false, SharedDbCredentials.Redact(ex.Message));
        }
    }

    /// <summary>Every table in the public schema. Backs the missing-table report above.</summary>
    public async Task<List<string>> ListTablesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>Row count for one table, or -1 if it can't be read.</summary>
    public async Task<long> CountRowsAsync(string table)
    {
        if (!IsSafeIdentifier(table)) return -1;
        try
        {
            await using var conn = await OpenAsync();
            // Identifier can't be parameterised; IsSafeIdentifier is the guard, and the name only
            // ever comes from pg_tables in the first place.
            await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.\"{table}\"", conn);
            var n = await cmd.ExecuteScalarAsync();
            return n is long l ? l : Convert.ToInt64(n ?? 0L);
        }
        catch { return -1; }
    }

    /// <summary>
    /// A table name is interpolated into the row-count query, so it gets a hard whitelist rather
    /// than trust. Everything legitimate comes back from <c>pg_tables</c>, but that's an
    /// invariant worth enforcing at the point of use.
    /// </summary>
    private static bool IsSafeIdentifier(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 63 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Postgres timestamps are <c>timestamptz</c>, and Npgsql 8 refuses a <see cref="DateTime"/>
    /// whose Kind isn't UTC. Values arriving from MongoDB are often <c>Unspecified</c> even though
    /// they are UTC in fact, so they're stamped rather than converted — converting would shift
    /// them by the local offset.
    /// </summary>
    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
}
