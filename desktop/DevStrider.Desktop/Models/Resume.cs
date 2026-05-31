using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A resume file (PDF/DOCX) the user produced for a specific bid. Convention is that the file
/// is named <c>"UID, Company, Role, Stack1, Stack2, … .pdf"</c> — we split on commas at upload
/// time so the parsed metadata is searchable and the UID is the link key between a bid and the
/// resume that was submitted for it.
/// </summary>
public class Resume
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Short alphanumeric identifier from the filename's first comma-segment, e.g. <c>7mK92</c>.</summary>
    public string Uid { get; set; } = "";

    /// <summary>Original filename including extension. Preserves the comma-separated naming convention.</summary>
    public string FileName { get; set; } = "";

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Stacks { get; set; } = new();

    /// <summary>e.g. <c>application/pdf</c>. Guessed from the file extension.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Raw file bytes (BSON binary). Small enough to inline for typical resumes (under 16 MB).</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
