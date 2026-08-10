using System.IO;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Resume files in Cloudflare R2, attached to scheduled interviews.
///
/// <para>
/// R2 exposes the S3 API, so this drives it with the AWS SDK: endpoint
/// <c>https://{accountId}.r2.cloudflarestorage.com</c>, region <c>auto</c> (R2 has no regions but
/// SigV4 demands one), and path-style addressing — R2 does not serve virtual-host style buckets.
/// </para>
///
/// <para>
/// Only the interview's own resume is stored here. The resume <i>text</i> already lives on the
/// interview row as <see cref="Interview.AttachedResumeContent"/>; this is the actual .docx/.pdf
/// a candidate walks into the interview with, which the Word macro wrote to disk and which
/// nothing was preserving.
/// </para>
/// </summary>
public sealed class R2StorageService
{
    /// <summary>Refuse anything larger. R2 is free-tier here and a resume is a small document.</summary>
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".pdf", ".docx", ".doc", ".rtf", ".txt", ".odt" };

    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public R2StorageService(SettingsService settings, ActivityLogService activity)
    {
        _settings = settings;
        _activity = activity;
    }

    public record Result(bool Ok, string Message);

    /// <summary>True once every field needed to reach a bucket is filled in.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var s = await _settings.GetAsync();
        return !string.IsNullOrWhiteSpace(s.R2AccountId)
            && !string.IsNullOrWhiteSpace(s.R2Bucket)
            && !string.IsNullOrWhiteSpace(s.R2AccessKeyId)
            && !string.IsNullOrWhiteSpace(s.R2SecretAccessKey);
    }

    private async Task<(AmazonS3Client? client, string bucket, string? error)> ConnectAsync()
    {
        var s = await _settings.GetAsync();
        if (!await IsConfiguredAsync())
            return (null, "", "Cloud storage isn't configured — Settings → Cloud storage (Cloudflare R2).");

        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{s.R2AccountId.Trim()}.r2.cloudflarestorage.com",
            // R2 has no regions, but SigV4 requires one in the signature; "auto" is what
            // Cloudflare documents. Path style because R2 does not serve bucket.host URLs.
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            Timeout = TimeSpan.FromSeconds(60),
        };

        var creds = new BasicAWSCredentials(s.R2AccessKeyId.Trim(), s.R2SecretAccessKey.Trim());
        return (new AmazonS3Client(creds, config), s.R2Bucket.Trim(), null);
    }

    /// <summary>
    /// Prove the credentials and bucket work, from Settings, rather than discovering it during
    /// an upload the user thought had succeeded.
    /// </summary>
    public async Task<Result> TestAsync()
    {
        var (client, bucket, error) = await ConnectAsync();
        if (client is null) return new Result(false, error!);

        using (client)
        {
            try
            {
                // Cheapest call that proves credentials, bucket existence and permission at once.
                await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
                return new Result(true, $"Connected to bucket '{bucket}'.");
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new Result(false, $"Bucket '{bucket}' not found on this account.");
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return new Result(false, "Access denied — check the API token has Object Read & Write on this bucket.");
            }
            catch (Exception ex)
            {
                return new Result(false, Describe(ex));
            }
        }
    }

    /// <summary>
    /// Object key for an interview's resume. Deterministic and one folder per interview, so a
    /// re-upload of the same filename replaces rather than accumulating, and the layout is
    /// readable in the R2 dashboard.
    /// </summary>
    public static string BuildKey(string username, Interview interview, string fileName)
    {
        var user = Sanitize(string.IsNullOrWhiteSpace(username) ? "unknown" : username);
        var safeName = Sanitize(Path.GetFileName(fileName));
        return $"resumes/{user}/{interview.Id}/{safeName}";
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    /// <summary>
    /// Upload <paramref name="localPath"/> and stamp the interview with where it landed. The
    /// caller persists the interview; this only mutates the in-memory object on success.
    /// </summary>
    public async Task<Result> UploadAsync(Interview interview, string localPath, string username)
    {
        if (!File.Exists(localPath)) return new Result(false, "That file no longer exists.");

        var info = new FileInfo(localPath);
        if (info.Length == 0) return new Result(false, "That file is empty.");
        if (info.Length > MaxUploadBytes)
            return new Result(false, $"File is {info.Length / (1024 * 1024)} MB; the limit is {MaxUploadBytes / (1024 * 1024)} MB.");

        var ext = Path.GetExtension(localPath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return new Result(false, $"'{ext}' isn't a resume format. Allowed: {string.Join(", ", AllowedExtensions)}");

        var (client, bucket, error) = await ConnectAsync();
        if (client is null) return new Result(false, error!);

        var key = BuildKey(username, interview, info.Name);
        using (client)
        {
            try
            {
                await using var stream = File.OpenRead(localPath);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = stream,
                    ContentType = ContentTypeFor(ext),
                    DisablePayloadSigning = true,   // R2 rejects streaming chunked signatures
                });
            }
            catch (Exception ex)
            {
                _activity.Error("Resume", "Upload failed", Describe(ex));
                return new Result(false, Describe(ex));
            }
        }

        interview.ResumeObjectKey = key;
        interview.ResumeFileName = info.Name;
        interview.ResumeSizeBytes = info.Length;
        interview.ResumeUploadedAt = DateTime.UtcNow;

        _activity.Success("Resume", "Uploaded to cloud", $"{info.Name} → {key}");
        return new Result(true, $"Uploaded {info.Name}.");
    }

    /// <summary>
    /// Fetch an object into a temp file and return its path. Used for both your own interviews
    /// and a peer's — the key is all that is needed, so nothing extra is required to read
    /// someone else's.
    /// </summary>
    public async Task<(string? path, string message)> DownloadToTempAsync(string objectKey, string fileName)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return (null, "No resume attached.");

        var (client, bucket, error) = await ConnectAsync();
        if (client is null) return (null, error!);

        // Own folder per download so two resumes with the same name can't collide.
        var dir = Path.Combine(Path.GetTempPath(), "devstrider-resumes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, string.IsNullOrWhiteSpace(fileName) ? "resume" : Sanitize(fileName));

        using (client)
        {
            try
            {
                using var response = await client.GetObjectAsync(bucket, objectKey);
                await response.WriteResponseStreamToFileAsync(target, false, CancellationToken.None);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (null, "That resume is no longer in cloud storage.");
            }
            catch (Exception ex)
            {
                _activity.Error("Resume", "Download failed", Describe(ex));
                return (null, Describe(ex));
            }
        }
        return (target, $"Downloaded {fileName}.");
    }

    /// <summary>Remove the object and clear the interview's pointer to it.</summary>
    public async Task<Result> DeleteAsync(Interview interview)
    {
        if (string.IsNullOrWhiteSpace(interview.ResumeObjectKey))
            return new Result(true, "Nothing attached.");

        var (client, bucket, error) = await ConnectAsync();
        if (client is null) return new Result(false, error!);

        using (client)
        {
            try
            {
                await client.DeleteObjectAsync(bucket, interview.ResumeObjectKey);
            }
            catch (Exception ex)
            {
                // Clearing the pointer anyway would strand the object; report instead.
                _activity.Error("Resume", "Delete failed", Describe(ex));
                return new Result(false, Describe(ex));
            }
        }

        _activity.Info("Resume", "Removed from cloud", interview.ResumeFileName);
        interview.ResumeObjectKey = "";
        interview.ResumeFileName = "";
        interview.ResumeSizeBytes = 0;
        interview.ResumeUploadedAt = null;
        return new Result(true, "Resume removed.");
    }

    private static string ContentTypeFor(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".rtf" => "application/rtf",
        ".txt" => "text/plain",
        ".odt" => "application/vnd.oasis.opendocument.text",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// A readable reason. The SDK's own messages bury the useful part, and an
    /// <see cref="AmazonS3Exception"/> for bad keys says "InvalidAccessKeyId" with a wall of
    /// request metadata around it.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        AmazonS3Exception s3 when s3.ErrorCode == "InvalidAccessKeyId" =>
            "Access key id is not recognised by this R2 account.",
        AmazonS3Exception s3 when s3.ErrorCode == "SignatureDoesNotMatch" =>
            "Secret access key is wrong.",
        AmazonS3Exception s3 when s3.ErrorCode == "NoSuchBucket" =>
            "That bucket does not exist on this account.",
        AmazonS3Exception s3 => $"{s3.ErrorCode}: {s3.Message}",
        System.Net.Http.HttpRequestException =>
            "Couldn't reach Cloudflare R2 — check the account id and your connection.",
        TaskCanceledException => "Timed out talking to Cloudflare R2.",
        _ => ex.Message,
    };
}
