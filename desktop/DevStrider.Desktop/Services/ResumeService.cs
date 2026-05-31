using System.IO;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class ResumeService
{
    private readonly MongoContext _db;
    public ResumeService(MongoContext db) => _db = db;

    public Task<List<Resume>> ListAsync() =>
        _db.Resumes
            .Find(FilterDefinition<Resume>.Empty)
            .SortByDescending(r => r.UploadedAt)
            .ToListAsync();

    /// <summary>Read the file at <paramref name="path"/>, parse its filename and persist.</summary>
    public async Task<Resume> AddFromFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var resume = ParseFileName(Path.GetFileName(path));
        resume.Bytes = bytes;
        await _db.Resumes.InsertOneAsync(resume);
        return resume;
    }

    public Task DeleteAsync(ObjectId id) =>
        _db.Resumes.DeleteOneAsync(r => r.Id == id);

    /// <summary>
    /// Find a resume by its UID (case-insensitive). The UID lives on the bid as the
    /// <c>resumeId</c> field, so this is the lookup path from interview → bid → resume.
    /// </summary>
    public Task<Resume?> GetByUidAsync(string uid) =>
        _db.Resumes
            .Find(r => r.Uid.ToLower() == uid.ToLower())
            .FirstOrDefaultAsync()!;

    /// <summary>Write the resume bytes to disk at <paramref name="destPath"/>.</summary>
    public async Task SaveToFileAsync(Resume resume, string destPath)
    {
        await File.WriteAllBytesAsync(destPath, resume.Bytes);
    }

    /// <summary>Resumes uploaded between <paramref name="fromUtc"/> and <paramref name="toUtc"/>.</summary>
    public Task<List<Resume>> ListInRangeAsync(DateTime fromUtc, DateTime toUtc) =>
        _db.Resumes
            .Find(r => r.UploadedAt >= fromUtc && r.UploadedAt < toUtc)
            .ToListAsync();

    /// <summary>
    /// Parse a filename of the form <c>"UID, Company, Role, Stack1, Stack2, ….ext"</c> into
    /// structured fields. Lenient: missing trailing pieces default to empty rather than throwing.
    /// </summary>
    public static Resume ParseFileName(string fileName)
    {
        var nameOnly = Path.GetFileNameWithoutExtension(fileName);
        var parts = nameOnly
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        return new Resume
        {
            FileName = fileName,
            Uid = parts.Length > 0 ? parts[0] : "",
            Company = parts.Length > 1 ? parts[1] : "",
            Role = parts.Length > 2 ? parts[2] : "",
            Stacks = parts.Length > 3 ? parts.Skip(3).ToList() : new List<string>(),
            ContentType = GuessContentType(fileName),
            UploadedAt = DateTime.UtcNow
        };
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => "application/pdf",
            ".doc"  => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt"  => "text/plain",
            ".md"   => "text/markdown",
            _       => "application/octet-stream"
        };
    }
}
