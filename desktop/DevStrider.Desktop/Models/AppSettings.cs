using CommunityToolkit.Mvvm.ComponentModel;

namespace DevStrider.Desktop.Models;

/// <summary>
/// Machine-level settings. Persisted as JSON on this machine by
/// <see cref="Services.SettingsStore"/>, because it says how to reach the portal and so has to be
/// readable before any request is made. Unknown fields are tolerated, so a settings file written
/// by an older install still deserializes — which is what happens to the six <c>SharedDb*</c>
/// fields this used to carry.
///
/// <para>
/// Nothing here is a secret any more. The one credential this app held was the shared PostgreSQL
/// password, sitting in cleartext in this file on every machine, and it went with the direct
/// database connection it opened. What is stored now is only what is true of this machine — the
/// portal's address, which port the listener binds, which profile was last open. The one thing
/// that is neither a setting nor public, the session token, has its own encrypted file
/// (<see cref="Services.SessionStore"/>).
/// </para>
/// </summary>
public class AppSettings
{
    /// <summary>
    /// The company portal, e.g. <c>https://triospace.org/hr</c>. Everything this app reads and
    /// writes goes through it: there is no second way in, and no database credential on this
    /// machine to be one.
    ///
    /// <para>
    /// Kept as typed and normalised at the point of use — see
    /// <see cref="Services.PortalApi.ParseBaseUrl"/> — so what comes back out of the Settings form
    /// is what the user put into it.
    /// </para>
    /// </summary>
    public string PortalBaseUrl { get; set; } = "";

    /// <summary>
    /// Port the local Bid-Assistant listener binds to (loopback only). Default 8765 — keep in
    /// sync with the Chrome extension's configured base URL. Localhost binding means no
    /// authentication is required.
    /// </summary>
    public int ListenerPort { get; set; } = 8765;

    /// <summary>
    /// Active <see cref="Profile"/> id. Set by the title-bar switcher; persisted so the next
    /// launch opens the same identity. <see cref="ObjectId.Empty"/> on a fresh install — the
    /// migration creates a "Default" profile and stamps it here.
    /// </summary>
    public ObjectId ActiveProfileId { get; set; }

    /// <summary>
    /// Legacy single-profile Word path. Kept so the first-launch migration can copy it into
    /// the seed <see cref="Profile.WordDocPath"/>; no longer read at runtime once a profile
    /// exists. Safe to remove a release or two after every install has run the migration.
    /// </summary>
    public string WordDocPath { get; set; } = "";

    /// <summary>Hotkey assigned to the Word macro. Default F9 triggers field updates.</summary>
    public string WordHotkey { get; set; } = "F9";

    /// <summary>
    /// Per-profile preferences for the user-driven ChatGPT Resume Studio. They contain no
    /// credentials and are machine preferences, so they belong beside the active-profile choice.
    /// </summary>
    public Dictionary<string, ChatGptResumeSessionSettings> ChatGptResumeSessions { get; set; } = new();

    /// <summary>Maximum successful resume generations kept in one ChatGPT conversation.</summary>
    public int ResumeGenerationsPerChat { get; set; } = 10;

    /// <summary>
    /// How many filled applications may wait for review at once, each in its own tab.
    ///
    /// <para>
    /// Reviewing is the slow part of a batch and it needs a person, so the run parks finished
    /// applications and carries on rather than waiting. Every parked tab is a live browser holding a
    /// rendered page, so this is a memory ceiling as much as an attention one; at the limit the run
    /// pauses and resumes as soon as a tab is closed.
    /// </para>
    /// </summary>
    public int MaxReviewTabs { get; set; } = 4;

    /// <summary>Must match the Word macro's OUTPUT_ROOT when automatic upload is desired.</summary>
    /// <summary>
    /// Legacy. These three moved onto <see cref="Profile"/>, where they belong - each profile drives
    /// its own Word document, so its OUTPUT_ROOT, FILE_BASE and salary answer are its own. They
    /// remain here only as the source for the one-time hand-over in ProfileContext, which clears
    /// them once every profile has a copy. Nothing reads them after that.
    /// </summary>
    public string ResumeOutputRoot { get; set; } = "";

    /// <summary>Must match the Word macro's FILE_BASE constant.</summary>
    public string ResumeOutputFileBase { get; set; } = "Resume";

    /// <summary>
    /// User-supplied salary or compensation expectation for application questions. Kept as free
    /// text so the user can include currency, range, and period (for example USD 140k-160k/year).
    /// The form adapter never invents this value; an empty setting leaves salary questions open.
    /// </summary>
    public string SalaryExpectation { get; set; } = "";

    /// <summary>
    /// Legacy answer bank, kept only so <see cref="Services.FormAnswerService"/> can move it into
    /// <c>ds_form_answers</c> once and then empty it. Answers live in the shared database now, so
    /// they follow the account between machines instead of sitting in one settings.json. Nothing
    /// writes here any more; do not add to it.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> JobFormAnswers { get; set; } = new();

    /// <summary>Per-profile, local job links to process one application at a time.</summary>
    public Dictionary<string, List<JobLinkQueueItem>> JobLinkQueues { get; set; } = new();

    // ── Cloudflare R2 (resume file storage) ─────────────────────────────────
    // Same rule as the shared-database credential above: stored in this file and loaded once at
    // startup by SettingsService, never re-read per use.

    /// <summary>
    /// R2 account id — the hex prefix of the S3 endpoint
    /// (<c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>). Not a secret.
    /// </summary>
    public string R2AccountId { get; set; } = "";

    /// <summary>Bucket holding uploaded resume files. Not a secret.</summary>
    public string R2Bucket { get; set; } = "";

    /// <summary>R2 API token access key id. Half of the credential pair.</summary>
    public string R2AccessKeyId { get; set; } = "";

    /// <summary>
    /// R2 API token secret. Stored in cleartext like every other credential here — and note that
    /// a token with object-write permission can also *delete* objects, so every install holding
    /// this can wipe the bucket.
    /// </summary>
    public string R2SecretAccessKey { get; set; } = "";

    /// <summary>S3-compatible endpoint derived from <see cref="R2AccountId"/>. Empty until set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string R2Endpoint =>
        string.IsNullOrWhiteSpace(R2AccountId) ? "" : $"https://{R2AccountId.Trim()}.r2.cloudflarestorage.com";

    // -- Proxy (ChatGPT reachability) ----------------------------------------
    // ChatGPT is unavailable from some of the places people run this from, and the whole app is
    // built around driving it through a real browser. A proxy is the difference between the app
    // working there and not working at all.

    /// <summary>Route browser traffic through <see cref="ProxyAddress"/>.</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>
    /// Proxy to route through, as <c>scheme://host:port</c>. <c>http</c>, <c>https</c>,
    /// <c>socks4</c> and <c>socks5</c> are the schemes Chromium accepts; a bare host:port is
    /// treated as http, which is what most people mean.
    /// </summary>
    public string ProxyAddress { get; set; } = "";

    /// <summary>
    /// Which browsers go through it. <c>chatgpt</c> - the default - leaves job sites on the direct
    /// connection, because they are reachable already and every extra hop is latency on the part of
    /// the run that does the most page work. <c>all</c> routes both.
    /// </summary>
    public string ProxyScope { get; set; } = Services.ProxyScopes.ChatGpt;

    /// <summary>Username for a proxy that asks for one. Blank means it does not.</summary>
    public string ProxyUsername { get; set; } = "";

    /// <summary>
    /// Proxy password, stored in cleartext beside the other credentials in this file. Only ever
    /// handed to the proxy that asked for it, and never written to the trace or the activity log.
    /// </summary>
    public string ProxyPassword { get; set; } = "";

    /// <summary>
    /// Hosts to reach directly, comma-separated, Chromium bypass syntax
    /// (<c>&lt;local&gt;, *.corp.example, 10.0.0.0/8</c>). The portal is added automatically.
    /// </summary>
    public string ProxyBypassList { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Shallow copy for editing. <see cref="SettingsService"/> hands the cached instance to
    /// read-only consumers, so the Settings form must work on a copy — otherwise every keystroke
    /// in the form would be visible to the listener and every other service before Save.
    /// Every property here is a value type or string, so a member-wise copy is a full copy.
    /// </summary>
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.ChatGptResumeSessions = (ChatGptResumeSessions ?? new()).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone());
        clone.JobFormAnswers = (JobFormAnswers ?? new()).ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase));
        clone.JobLinkQueues = (JobLinkQueues ?? new()).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(item => item.Clone()).ToList());
        return clone;
    }
}

/// <summary>Non-secret local preferences for one profile's ChatGPT UI generation session.</summary>
public sealed class ChatGptResumeSessionSettings
{
    // Kept for settings-file compatibility. New code reads AppSettings.ResumeGenerationsPerChat.
    public int GenerationLimit { get; set; } = 10;
    public bool AutomaticallyRunWordMacro { get; set; }
    public bool AutomaticallySubmitChatGptPrompt { get; set; }

    /// <summary>
    /// The <c>/c/…</c> conversation the resume chat is running in.
    ///
    /// <para>
    /// Persisted because the profile prompt is sent once, at the start of a chat, and every later
    /// resume relies on that context. Without a note of which conversation it was, a follow-up went
    /// to whatever the ChatGPT pane happened to be showing — so one click in its sidebar sent the
    /// next job description into an unrelated chat that had never seen the resume instructions.
    /// </para>
    /// </summary>
    public string ResumeConversationUrl { get; set; } = "";

    public ChatGptResumeSessionSettings Clone() => new()
    {
        GenerationLimit = GenerationLimit,
        AutomaticallyRunWordMacro = AutomaticallyRunWordMacro,
        AutomaticallySubmitChatGptPrompt = AutomaticallySubmitChatGptPrompt,
        ResumeConversationUrl = ResumeConversationUrl,
    };
}

/// <summary>A persisted application work item moving through the automatic/recovery pipeline.</summary>
public sealed partial class JobLinkQueueItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string Intent { get; set; } = JobWorkItemIntents.Apply;
    public string JobDescription { get; set; } = "";
    public string FormQuestionsJson { get; set; } = "[]";
    public string AnswersJson { get; set; } = "{}";
    public string ResumeFilePath { get; set; } = "";
    public string BidId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string Error { get; set; } = "";

    /// <summary>
    /// The ChatGPT conversation used to answer this application's questions. The conversation id
    /// is stored separately for diagnostics; the URL is the durable value WebView2 can reopen.
    /// </summary>
    public string AnswerConversationId { get; set; } = "";
    public string AnswerConversationUrl { get; set; } = "";
    public int AnswerCorrectionAttempts { get; set; }
    public string PendingCorrectionQuestionsJson { get; set; } = "[]";

    /// <summary>Survives requeues so a link that keeps failing is visible as such rather than looping silently.</summary>
    public int Attempts { get; set; }

    [ObservableProperty]
    private string _status = JobLinkQueueStatuses.Queued;

    public JobLinkQueueItem Clone() => new()
    {
        Id = Id,
        Url = Url,
        AddedAt = AddedAt,
        UpdatedAt = UpdatedAt,
        Intent = Intent,
        JobDescription = JobDescription,
        FormQuestionsJson = FormQuestionsJson,
        AnswersJson = AnswersJson,
        ResumeFilePath = ResumeFilePath,
        BidId = BidId,
        AdapterName = AdapterName,
        Error = Error,
        AnswerConversationId = AnswerConversationId,
        AnswerConversationUrl = AnswerConversationUrl,
        AnswerCorrectionAttempts = AnswerCorrectionAttempts,
        PendingCorrectionQuestionsJson = PendingCorrectionQuestionsJson,
        Attempts = Attempts,
        Status = Status,
    };
}

public static class JobWorkItemIntents
{
    public const string Apply = "Apply";
    public const string ResumeOnly = "Resume only";
}

public static class JobLinkQueueStatuses
{
    public const string Queued = "Queued";
    public const string Loading = "Loading job";
    public const string ExtractingJobDescription = "Extracting JD";
    public const string NeedsJobDescription = "Needs JD";
    public const string GeneratingResume = "Generating resume";
    public const string CreatingDocument = "Creating document";
    public const string FillingApplication = "Filling application";
    public const string ResolvingApplicationFields = "Resolving application fields";
    public const string ReadyForReview = "Ready for review";
    public const string Submitted = "Submitted";
    public const string ResumeReady = "Resume ready";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    // Compatibility with queues persisted by earlier builds.
    public const string InProgress = "In progress";
    public const string Completed = "Completed";
}
