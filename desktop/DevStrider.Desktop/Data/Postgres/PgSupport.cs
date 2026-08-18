using DevStrider.Desktop.Services;
using MongoDB.Bson;
using Npgsql;
using NpgsqlTypes;

namespace DevStrider.Desktop.Data.Postgres;

/// <summary>
/// Conversions every repository needs. Kept in one place so "how is an ObjectId stored" has
/// exactly one answer.
/// </summary>
public static class Pg
{
    /// <summary>ObjectId as its 24-character hex string.</summary>
    public static string Hex(ObjectId id) => id == ObjectId.Empty ? "" : id.ToString();

    /// <summary>
    /// Back to an ObjectId. Anything unparseable — including the empty string this writes for
    /// "none" — reads as <see cref="ObjectId.Empty"/>, which is what the models mean by it.
    /// </summary>
    public static ObjectId Oid(string? hex) =>
        !string.IsNullOrEmpty(hex) && ObjectId.TryParse(hex, out var id) ? id : ObjectId.Empty;

    public static string Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    public static ObjectId OidAt(NpgsqlDataReader r, int i) => Oid(r.IsDBNull(i) ? null : r.GetString(i));

    public static ObjectId? NullableOidAt(NpgsqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var id = Oid(r.GetString(i));
        return id == ObjectId.Empty ? null : id;
    }

    public static DateTime? NullableDateAt(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetDateTime(i);

    public static int? NullableIntAt(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetInt32(i);

    public static List<string> StringsAt(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? new List<string>() : ((string[])r.GetValue(i)).ToList();

    /// <summary>A <c>text[]</c> parameter. Null lists are sent as an empty array, never NULL.</summary>
    public static NpgsqlParameter Array(string name, IEnumerable<string>? values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (values ?? Enumerable.Empty<string>()).ToArray() };

    /// <summary>NULL for an absent timestamp, UTC-stamped otherwise.</summary>
    public static object NullableUtc(DateTime? value) =>
        value.HasValue ? SharedDbContext.Utc(value.Value) : DBNull.Value;

    public static object NullableInt(int? value) => value.HasValue ? value.Value : DBNull.Value;

    /// <summary>Empty text as NULL — for columns where "none" is genuinely absent, not ''.</summary>
    public static object NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    public static long AsLong(object? scalar) =>
        scalar is long l ? l : Convert.ToInt64(scalar ?? 0L);
}

/// <summary>
/// Shared plumbing for the repositories: open a connection, run one statement, map the rows.
///
/// <para>
/// No connection is held open. Npgsql pools underneath, so opening per operation costs nothing and
/// avoids holding a slot on an instance that caps connections.
/// </para>
///
/// <para>
/// <see cref="UserId"/> is the account every query filters on. It comes from the session rather
/// than from the caller — see the note on <see cref="IAccountRepository"/> — and reading it throws
/// if nothing is signed in, which is the intended outcome: a query that quietly ran unscoped would
/// either show an empty app or somebody else's work.
/// </para>
/// </summary>
public abstract class PgRepository
{
    private readonly SharedDbContext _db;
    private readonly SessionContext _session;

    protected PgRepository(SharedDbContext db, SessionContext session)
    {
        _db = db;
        _session = session;
    }

    /// <summary>The signed-in <c>app_user.id</c>. Throws if there isn't one.</summary>
    protected long UserId => _session.Require();

    protected async Task<List<T>> ListAsync<T>(
        string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, T> map)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await r.ReadAsync()) rows.Add(map(r));
        return rows;
    }

    protected async Task<T?> FirstOrDefaultAsync<T>(
        string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, T> map) where T : class
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? map(r) : null;
    }

    protected async Task<long> CountAsync(string sql, Action<NpgsqlCommand> bind)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        return Pg.AsLong(await cmd.ExecuteScalarAsync());
    }

    protected async Task<int> ExecuteAsync(string sql, Action<NpgsqlCommand> bind)
    {
        await using var conn = await _db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        return await cmd.ExecuteNonQueryAsync();
    }

}
