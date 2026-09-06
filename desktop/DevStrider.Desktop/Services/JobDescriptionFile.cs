using System.IO;
using System.Text;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Writes the job description as a text file into the folder the Word macro just produced the
/// resume in, so a bid's folder holds both halves of what it was: the resume that was sent, and
/// the posting it was tailored to.
///
/// <para>
/// <b>Why the app writes this and not the macro.</b> The macro can — that is what the second
/// argument added in 9.0 is for (<c>SaveResumeAutomatically</c> in desktop/macro.md) — but only a
/// template that has been updated to accept it, and an un-updated one silently produces no file at
/// all. Doing it here makes the job-description file independent of which signature a profile's
/// <c>.docm</c> is on. A two-argument template that also writes it just gets the same bytes written
/// over the top a moment later; set <c>SAVE_JOB_DESCRIPTION = False</c> in the macro to leave the
/// job entirely to this.
/// </para>
///
/// <para>
/// <b>The path is composed, not discovered.</b> The base is the directory holding the profile's
/// template, and the leaf is the <c>[FolderName]:</c> line from the reply — the same string the
/// macro names its output folder with. The macro's own <c>OUTPUT_ROOT</c> is a
/// <c>Private Const</c> inside the VBA that nothing out here can read, so this assumes the two
/// agree, which is how every template in desktop/macro.md is configured. When they don't, the
/// folder simply isn't there and the caller is told which path was tried — nothing is created, so
/// a mismatch leaves an obvious gap rather than a stray folder somewhere plausible.
/// </para>
/// </summary>
public static class JobDescriptionFile
{
    /// <summary>
    /// Matches <c>JD_FILE_NAME</c>'s default in desktop/macro.md, so an updated template and this
    /// write to one file rather than two.
    /// </summary>
    public const string FileName = "Job Description.txt";

    /// <param name="Written">Whether a file was actually written.</param>
    /// <param name="Path">The path tried — set even on failure, because "which path?" is the
    /// question a failure raises.</param>
    /// <param name="Message">Human-readable outcome, ready for the Activity log.</param>
    public sealed record Result(bool Written, string Path, string Message);

    /// <summary>
    /// Save <paramref name="jobDescription"/> beside the resume. Never throws: every failure is a
    /// <see cref="Result"/> with <see cref="Result.Written"/> false, because the bid and the resume
    /// are already done by the time this runs and neither should be reported as failed over a
    /// sidecar file.
    /// </summary>
    public static Result Save(string? wordDocPath, string? folderName, string? jobDescription)
    {
        var jd = jobDescription ?? "";
        if (jd.Trim().Length == 0)
            return new Result(false, "", "No job description was captured for this bid.");

        var baseDir = TemplateDirectory(wordDocPath);
        if (baseDir == null)
            return new Result(false, "",
                "The profile has no Word template path, so there is no output folder to write beside.");

        var folder = SanitizeFolderName(folderName);
        if (folder.Length == 0)
            return new Result(false, "",
                "The reply carried no [FolderName]: line, so the resume folder can't be identified.");

        var dir = System.IO.Path.Combine(baseDir, folder);
        if (!Directory.Exists(dir))
            return new Result(false, dir,
                $"No resume folder at {dir}. Either the macro fell back to its FALLBACK_FOLDER name, " +
                "or its OUTPUT_ROOT points somewhere other than the template's own folder.");

        var path = System.IO.Path.Combine(dir, FileName);
        try
        {
            // UTF-8 with a BOM: job descriptions are full of the characters that made the old
            // clipboard route corrupt resumes — em-dashes, smart quotes, accented names — and the
            // BOM is what stops a Windows text editor guessing ANSI and rendering them as noise.
            File.WriteAllText(path, jd, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return new Result(true, path, path);
        }
        catch (Exception ex)
        {
            return new Result(false, path, $"Couldn't write {path} — {ex.Message}");
        }
    }

    /// <summary>
    /// The fast-feed line as a folder name, matching what the macro does to it. A stack list is
    /// the usual source of trouble: "GitLab CI/CD" is a legal thing to write on a resume and an
    /// illegal thing to put in a path, and the folders on disk show the macro turning it into
    /// "GitLab CI-CD". Every invalid character goes the same way, so a name composed here lands on
    /// the folder the macro actually made.
    /// </summary>
    public static string SanitizeFolderName(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) return "";

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);

        // Windows drops trailing dots and spaces from directory names without telling anyone, so a
        // name ending in either would never match the folder it created.
        return sb.ToString().TrimEnd(' ', '.');
    }

    /// <summary>The folder holding the profile's <c>.docm</c>, or null when the path is unusable.</summary>
    private static string? TemplateDirectory(string? wordDocPath)
    {
        var p = (wordDocPath ?? "").Trim();
        if (p.Length == 0) return null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p));
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
