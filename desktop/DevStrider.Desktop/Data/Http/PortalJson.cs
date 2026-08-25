using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Data.Http;

/// <summary>
/// How this app and the portal agree to spell a row on the wire.
///
/// <para>
/// The models go over the wire as they are — there is no DTO layer, because a second set of
/// classes whose only job is to have the same fields is a second place for a field to be
/// forgotten. Camel-case naming lines the C# properties up with the JSON the portal emits, and
/// the two converters below cover the only two types whose default rendering would be wrong.
/// </para>
/// </summary>
public static class PortalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // The portal is free to add a field before this app knows about it. Refusing to
        // deserialize a response because it grew is not a useful failure.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new ObjectIdApiConverter(), new UtcDateTimeConverter() },
    };
}

/// <summary>
/// <see cref="ObjectId"/> as the <c>ds_*</c> tables store it: 24 lower-case hex characters, and
/// the <b>empty string</b> for "none".
///
/// <para>
/// Not the 24 zeros <see cref="ObjectId.ToString"/> produces. That difference is load-bearing:
/// <c>profile_id</c> holds <c>''</c> for a row nothing has been assigned to yet, and the repair
/// that stamps a profile onto those rows matches on <c>profile_id = ''</c>. Sending zeros would
/// write a row that looks assigned, to a profile that does not exist, and no repair would ever
/// find it again.
/// </para>
/// </summary>
public sealed class ObjectIdApiConverter : JsonConverter<ObjectId>
{
    public override ObjectId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ObjectId.Empty;
        return ObjectId.TryParse(reader.GetString(), out var id) ? id : ObjectId.Empty;
    }

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsEmpty ? "" : value.ToString());
}

/// <summary>
/// A timestamp, always written with an explicit <c>Z</c>.
///
/// <para>
/// The default writer renders a <see cref="DateTimeKind.Unspecified"/> value with no offset at
/// all, and the portal parses that as its own local time — so a row saved from a machine in
/// UTC+9 would land nine hours out, silently, and only on the fields that happened to be
/// Unspecified. Kind is treated as an assertion about how the value was built, not as something
/// to convert by: these values are UTC in fact, whatever they claim, which is the same reading
/// the direct-to-Postgres code used before this.
/// </para>
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : default;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Utc(value).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));

    /// <summary>Stamped as UTC, never converted to it — see the note on the class.</summary>
    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
}
