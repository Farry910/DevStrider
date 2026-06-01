using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace DevStrider.Desktop.Models;

/// <summary>
/// Accepts <see cref="List{T}"/> of strings stored as a BSON array (the canonical form), a
/// comma-separated string (legacy data carried over from an earlier model that typed the
/// field as <c>string</c>), or null. Always writes back as a BSON array, so a save after a
/// load quietly migrates the legacy string form into the canonical one.
/// </summary>
public sealed class FlexibleStringListSerializer : SerializerBase<List<string>>
{
    public override List<string> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var type = reader.GetCurrentBsonType();
        switch (type)
        {
            case BsonType.Null:
                reader.ReadNull();
                return new List<string>();

            case BsonType.String:
                var raw = reader.ReadString();
                if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
                return raw.Split(',')
                          .Select(s => s.Trim())
                          .Where(s => s.Length > 0)
                          .ToList();

            case BsonType.Array:
                reader.ReadStartArray();
                var list = new List<string>();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    // Be defensive: tolerate the occasional non-string entry rather than
                    // throw on the whole document.
                    var t = reader.GetCurrentBsonType();
                    if (t == BsonType.String) list.Add(reader.ReadString());
                    else if (t == BsonType.Null) { reader.ReadNull(); }
                    else reader.SkipValue();
                }
                reader.ReadEndArray();
                return list;

            default:
                // Unknown shape — preserve forward progress: skip and return empty.
                reader.SkipValue();
                return new List<string>();
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<string> value)
    {
        var writer = context.Writer;
        if (value == null) { writer.WriteNull(); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteString(s ?? "");
        writer.WriteEndArray();
    }
}
