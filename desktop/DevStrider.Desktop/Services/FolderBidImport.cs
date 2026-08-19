using System.IO;
using System.Security.Cryptography;
using System.Text;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One folder that scanned as a bid, or didn't. <paramref name="CreatedAt"/> is the folder's own
/// creation time — the moment the macro wrote it, which is the moment the bid was made.
/// </summary>
public sealed record FolderBidCandidate(string FolderName, DateTime CreatedAt, FastFeed.Parsed? Parsed)
{
    public bool Ok => Parsed != null;
}

/// <summary>What a scan found, before anything is recorded.</summary>
public sealed record FolderScanResult(
    IReadOnlyList<FolderBidCandidate> Candidates, string Message)
{
    public int Recognised => Candidates.Count(c => c.Ok);
    public int Skipped => Candidates.Count - Recognised;

    /// <summary>
    /// The span the recognised folders were created over, for the dialog to show. Seeing this is
    /// the check that the timestamps are the real ones: folders that were copied or restored carry
    /// the date of the copy, and a batch that all reads "today" is the tell.
    /// </summary>
    public (DateTime from, DateTime to)? DateRange
    {
        get
        {
            var dates = Candidates.Where(c => c.Ok).Select(c => c.CreatedAt).ToList();
            return dates.Count == 0 ? null : (dates.Min(), dates.Max());
        }
    }
}

/// <summary>
/// Records a day's bids in bulk from the resume folders the Word macro left on disk.
///
/// <para>
/// The macro names each output folder with the fast-feed line — <c>UID, Company, Role, Stack1,
/// …</c> — so a directory of them is already a list of bids in everything but name. Pointing at
/// that directory is a back door for getting a day's work onto the board when the extension didn't
/// record it: the machine was offline, the app wasn't running, someone bid from a different
/// machine, or the bids predate the app entirely.
/// </para>
///
/// <para>
/// <b>What is lost is real and worth knowing.</b> A folder name carries the resume id, company,
/// role and stacks — and nothing else. There is no URL and no job description on these rows, so
/// they take no part in duplicate-URL detection and the JD viewer has nothing to show. That is the
/// accepted trade for entering bids that would otherwise not be recorded at all.
/// </para>
///
/// <para>
/// Re-scanning the same folder is safe. Each row's id is derived from the profile, the folder name
/// and the date rather than generated fresh, so a second import of the same directory updates the
/// same rows instead of doubling them — which matters, because "did that actually work?" is the
/// exact moment someone clicks Import twice.
/// </para>
/// </summary>
public sealed class FolderBidImport
{
    private readonly BidBoardService _board;
    private readonly ActivityLogService _activity;

    public FolderBidImport(BidBoardService board, ActivityLogService activity)
    {
        _board = board;
        _activity = activity;
    }

    /// <summary>
    /// Read the folder names and work out which ones are bids. Touches nothing but the directory
    /// listing — no files are opened and nothing is recorded.
    /// </summary>
    public FolderScanResult Scan(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return new FolderScanResult(Array.Empty<FolderBidCandidate>(), "Pick a folder first.");

        if (!Directory.Exists(folderPath))
            return new FolderScanResult(Array.Empty<FolderBidCandidate>(), "That folder doesn't exist.");

        List<FolderBidCandidate> candidates;
        try
        {
            candidates = new DirectoryInfo(folderPath).EnumerateDirectories()
                // Creation time, not last-write: a directory's write time moves every time a file
                // lands in it, so it tracks when the macro *finished*, and keeps moving if anything
                // touches the folder afterwards. Creation is the moment the bid was made.
                .Select(d => new FolderBidCandidate(d.Name, d.CreationTime, FastFeed.ParseLine(d.Name)))
                .OrderBy(c => c.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            return new FolderScanResult(Array.Empty<FolderBidCandidate>(), $"Couldn't read that folder: {ex.Message}");
        }

        if (candidates.Count == 0)
            return new FolderScanResult(Array.Empty<FolderBidCandidate>(),
                "No sub-folders in there. Pick the folder that *contains* the resume folders, not one of them.");

        var ok = candidates.Count(c => c.Ok);
        var skipped = candidates.Count - ok;
        var message = ok == 0
            ? $"None of the {candidates.Count} sub-folders are named like a fast-feed line "
              + "(UID, Company, Role, …). Nothing to record."
            : $"{ok} of {candidates.Count} sub-folder{(candidates.Count == 1 ? "" : "s")} will be recorded"
              + (skipped == 0 ? "" : $"; {skipped} skipped — not named like a fast-feed line")
              + ".";

        var result = new FolderScanResult(candidates, message);
        if (result.DateRange is { } range)
        {
            var when = range.from.Date == range.to.Date
                ? $"Dated {range.from:yyyy-MM-dd}, {range.from:HH:mm}–{range.to:HH:mm}."
                : $"Dated {range.from:yyyy-MM-dd HH:mm} to {range.to:yyyy-MM-dd HH:mm}.";
            result = result with { Message = $"{message} {when}" };
        }
        return result;
    }

    /// <summary>
    /// Record every recognised folder as a bid under <paramref name="profileId"/>, each dated by
    /// its own folder.
    ///
    /// <para>
    /// The macro writes a folder at the moment it finishes a resume, so the folder's creation time
    /// <i>is</i> when that bid was made — per bid, not per batch. That precision is not decorative:
    /// the bids-per-10-minute chart buckets on <see cref="UserBid.AppliedAt"/>, and one date for a
    /// whole day's folders would stack every bid into a single bar.
    /// </para>
    ///
    /// <para>
    /// The timestamps are only as true as the folders. Copied or restored directories carry the
    /// date of the copy, not of the work — which is why <see cref="Scan"/> reports the range it
    /// found, so a batch that all reads "today" is visible before anything is written.
    /// </para>
    /// </summary>
    public async Task<int> ImportAsync(FolderScanResult scan, ObjectId profileId)
    {
        if (profileId == ObjectId.Empty) throw new InvalidOperationException("Pick a profile first.");

        var recorded = 0;
        foreach (var candidate in scan.Candidates.Where(c => c.Ok))
        {
            var parsed = candidate.Parsed!;
            // The folder time is local; the columns are timestamptz. Convert rather than stamp —
            // this is a real local wall-clock reading, not a UTC value wearing the wrong Kind.
            var when = DateTime.SpecifyKind(candidate.CreatedAt, DateTimeKind.Local).ToUniversalTime();

            var bid = new UserBid
            {
                Id = StableId(profileId, candidate.FolderName),
                ProfileId = profileId,
                ResumeId = parsed.ResumeId,
                Company = parsed.Company,
                Role = parsed.Role,
                PrimaryStacks = parsed.PrimaryStacks.ToList(),
                Status = BidStatuses.Applied,
                Origin = "Folder import",
                // No Url, no UrlNorm, no JobDescription. A folder name has none of them.
                CreatedAt = when,
                UpdatedAt = when,
                AppliedAt = when,
            };
            await _board.RecordAsync(bid);
            recorded++;
        }

        if (recorded > 0)
        {
            var range = scan.DateRange;
            _activity.Success("Bids", $"Recorded {recorded} bid{(recorded == 1 ? "" : "s")} from folders",
                range is { } r ? $"Dated {r.from:yyyy-MM-dd HH:mm} to {r.to:yyyy-MM-dd HH:mm}." : "");
        }
        return recorded;
    }

    /// <summary>
    /// A repeatable id for a (profile, folder). Everything else in this app keeps an id it was
    /// given; these rows have no prior identity, so one is derived from what identifies them —
    /// which is what makes a second import an update rather than a duplicate.
    ///
    /// <para>
    /// The folder name alone, deliberately, with no timestamp in it. The name already carries the
    /// resume id, which is unique per generated resume, and folding the date in would mean a folder
    /// whose timestamp shifted — copied to another machine, restored from a backup — imported as a
    /// second row rather than matching the first.
    /// </para>
    /// </summary>
    private static ObjectId StableId(ObjectId profileId, string folderName)
    {
        var key = $"folder-bid|{profileId}|{folderName.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // ObjectId is 12 bytes; the first 12 of a SHA-256 are as good as any for uniqueness here.
        return new ObjectId(hash.AsSpan(0, 12).ToArray());
    }
}
