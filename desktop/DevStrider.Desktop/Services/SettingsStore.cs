using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON under
/// <c>%LOCALAPPDATA%\DevStrider\settings.json</c>.
///
/// <para>
/// Settings used to be a row in the local MongoDB. They can't stay there: the row holds the
/// shared database's credentials <i>and</i> the switch that says which database to use, so
/// reading it from a database is circular — in shared mode the app would need the connection
/// string in order to find out how to connect. A file on disk is the only place that answer can
/// live, and it is also what lets MongoDB be removed at the end of the migration.
/// </para>
///
/// <para>
/// Writes go to a temp file and are then moved over the target, so a crash mid-write leaves the
/// previous settings intact rather than a half-written file that fails to parse on next launch.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    /// <summary>
    /// Per-user, per-machine, and deliberately not roaming: this file names a local MongoDB and a
    /// listener port, neither of which means anything on another machine.
    /// </summary>
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevStrider");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Same tolerance the BSON conventions give: a field removed in a later release must not
        // make an older settings file unreadable.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new ObjectIdJsonConverter() },
    };

    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Load the file, or null when it isn't there or can't be parsed. Synchronous because the DI
    /// container is built from it before anything async has started.
    /// </summary>
    public AppSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<AppSettings>(text, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A settings file we can't read is not worth crashing over — the caller falls back to
            // defaults, which land the user in local mode with an empty form to fill in.
            System.Diagnostics.Debug.WriteLine($"settings.json unreadable: {ex.Message}");
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    public Task SaveAsync(AppSettings settings) => Task.Run(() => Save(settings));
}

/// <summary>
/// <see cref="ObjectId"/> as its 24-character hex string. System.Text.Json would otherwise
/// serialize its public properties (Timestamp, Machine, …) and fail to reconstruct it, which
/// would silently lose <see cref="AppSettings.ActiveProfileId"/> on every save.
/// </summary>
public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ObjectId.Empty;
        var raw = reader.GetString();
        return ObjectId.TryParse(raw, out var id) ? id : ObjectId.Empty;
    }

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
