using System.Globalization;
using System.Security.Cryptography;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A 12-byte identifier rendered as 24 lower-case hex characters.
///
/// <para>
/// This used to be <c>MongoDB.Bson.ObjectId</c>, which is why the format is what it is: every
/// <c>ds_*.id</c> in the shared database is one of these strings, carried over from the local
/// databases Postgres replaced. MongoDB itself went with the 8.0 migration, and the driver stayed
/// behind only to supply this one value type — so it is defined here instead, byte-for-byte
/// compatible, and the package is gone. Existing rows parse and compare exactly as before.
/// </para>
///
/// <para>
/// Layout matches the original: 4 bytes big-endian seconds since the Unix epoch, then 8 random
/// bytes. The timestamp prefix is not decorative — ids sort chronologically as text, which is what
/// makes <c>ORDER BY id</c> mean "oldest first" wherever the code leans on it.
/// </para>
/// </summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    private const int Size = 12;
    private readonly byte[]? _bytes;

    /// <summary>
    /// From 12 raw bytes. Used for deterministic ids — <see cref="Services.FolderBidImport"/>
    /// derives one from a SHA-256 of the folder name so re-importing the same folder matches the
    /// existing row instead of inserting a second.
    /// </summary>
    public ObjectId(byte[] bytes)
    {
        if (bytes is not { Length: Size })
            throw new ArgumentException($"An id is exactly {Size} bytes.", nameof(bytes));
        _bytes = bytes;
    }

    private ObjectId(byte[] bytes, bool _) => _bytes = bytes;

    /// <summary>All-zero id — what the models mean by "none".</summary>
    public static ObjectId Empty => default;

    public bool IsEmpty => _bytes == null || _bytes.All(b => b == 0);

    public static ObjectId GenerateNewId()
    {
        var bytes = new byte[Size];
        var seconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bytes[0] = (byte)(seconds >> 24);
        bytes[1] = (byte)(seconds >> 16);
        bytes[2] = (byte)(seconds >> 8);
        bytes[3] = (byte)seconds;
        RandomNumberGenerator.Fill(bytes.AsSpan(4));
        return new ObjectId(bytes, true);
    }

    public static bool TryParse(string? value, out ObjectId id)
    {
        id = Empty;
        if (value is not { Length: Size * 2 }) return false;
        var bytes = new byte[Size];
        for (var i = 0; i < Size; i++)
        {
            if (!byte.TryParse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var parsed)) return false;
            bytes[i] = parsed;
        }
        id = new ObjectId(bytes, true);
        return true;
    }

    public static ObjectId Parse(string? value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"Not a 24-character hex id: {value}");

    public override string ToString() =>
        _bytes == null ? new string('0', Size * 2) : Convert.ToHexString(_bytes).ToLowerInvariant();

    public bool Equals(ObjectId other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes == null) return 0;
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    /// <summary>Ordinal over the 12 bytes, so ordering matches ordering of the hex text.</summary>
    public int CompareTo(ObjectId other)
    {
        var left = _bytes ?? new byte[Size];
        var right = other._bytes ?? new byte[Size];
        return left.AsSpan().SequenceCompareTo(right.AsSpan());
    }

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
    public static bool operator <(ObjectId left, ObjectId right) => left.CompareTo(right) < 0;
    public static bool operator >(ObjectId left, ObjectId right) => left.CompareTo(right) > 0;
    public static bool operator <=(ObjectId left, ObjectId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ObjectId left, ObjectId right) => left.CompareTo(right) >= 0;
}
