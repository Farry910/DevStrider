using System.IO;
using System.Text.RegularExpressions;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The two kinds of work that drive ChatGPT, and the reason there is more than one browser.
///
/// <para>
/// An automatic run and a manual bid want the composer at the same time — that is the whole point
/// of a manual bid, which exists so the typing and the generating overlap. One browser cannot serve
/// both: the second request navigates the pane out from under the first, and the run that was
/// waiting on a reply reads whatever the other one produced.
/// </para>
/// </summary>
public static class ChatGptLanes
{
    /// <summary>The queue: extract, generate, fill, review.</summary>
    public const string Auto = "auto";

    /// <summary>A bid being filled in by hand, whose resume is written alongside it.</summary>
    public const string Manual = "manual";

    public static readonly string[] All = [Auto, Manual];

    public static string Label(string lane) => lane switch
    {
        Auto => "Automatic runs",
        Manual => "Manual bids",
        _ => lane,
    };

    public static bool IsKnown(string? lane) =>
        lane != null && All.Contains(lane, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One signed-in ChatGPT identity, which in practice means one WebView2 user-data folder.
///
/// <para>
/// The folder <i>is</i> the account: it holds the cookies, so two browsers pointed at the same
/// folder are the same logged-in user and two pointed at different folders are two different ones.
/// Nothing here holds a password — signing in happens in the browser, once, and persists because
/// the folder does.
/// </para>
/// </summary>
public sealed class ChatGptAccount
{
    /// <summary>
    /// Stable identity. Used as the folder name and as the value lanes are assigned to, so it must
    /// survive a rename — renaming the folder would sign the account out, which is precisely the
    /// cost this exists to avoid.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>What it is called in the UI. Free text, renameable, means nothing to the code.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Where its browser profile lives. Stored rather than derived so the first account can keep
    /// pointing at the pre-10.21 folder and nobody is signed out by upgrading.
    /// </summary>
    public string UserDataFolder { get; set; } = "";

    /// <summary>Free-text note — which e-mail address this is, whether it is a Plus account.</summary>
    public string Note { get; set; } = "";

    public ChatGptAccount Clone() => new()
    {
        Id = Id, Name = Name, UserDataFolder = UserDataFolder, Note = Note,
    };
}

/// <summary>
/// The management centre for ChatGPT identities: which accounts exist, where each one's browser
/// profile lives, and which kind of work signs in as which.
///
/// <para>
/// Two lanes on <b>different</b> accounts cannot interfere at all — separate cookie jars, separate
/// conversation lists, separate rate limits. Two lanes on the <b>same</b> account share everything,
/// which is fine for the browser and not fine for the conversation: both would continue the same
/// <c>/c/…</c> chat and interleave two jobs' messages into it. That case is what
/// <see cref="ChatGptConversationRegistry"/> arbitrates, and <see cref="LanesShareAnAccount"/> is
/// how the rest of the app knows it has to.
/// </para>
/// </summary>
public sealed class ChatGptAccountService
{
    /// <summary>
    /// The account the pre-10.21 single browser was using. Its folder is the one that already holds
    /// a signed-in session, so it is adopted rather than recreated.
    /// </summary>
    public const string DefaultAccountId = "default";

    private static readonly Regex NotSlug = new("[^a-z0-9-]", RegexOptions.Compiled);

    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public ChatGptAccountService(SettingsService settings, ActivityLogService activity)
    {
        _settings = settings;
        _activity = activity;
    }

    /// <summary>Raised after accounts or lane assignments change, so the UI can reload.</summary>
    public event Action? Changed;

    /// <summary>The folder the original single ChatGPT browser used, and still uses.</summary>
    public static string LegacyUserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevStrider", "webview2", "chatgpt");

    /// <summary>Where a new account's browser profile goes.</summary>
    public static string FolderForId(string accountId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevStrider", "webview2", $"chatgpt-{accountId}");

    // ── reading ─────────────────────────────────────────────────────────────

    public async Task<List<ChatGptAccount>> ListAsync()
    {
        var settings = await _settings.GetAsync();
        return Ensure(settings).Select(account => account.Clone()).ToList();
    }

    /// <summary>The account a lane signs in as. Never null — an unassigned lane falls to default.</summary>
    public async Task<ChatGptAccount> ForLaneAsync(string lane)
    {
        var settings = await _settings.GetAsync();
        return Resolve(settings, lane).Clone();
    }

    /// <summary>
    /// True when both lanes are the same signed-in user, and therefore share a conversation list.
    /// The one question the rest of the app actually needs answered.
    /// </summary>
    public async Task<bool> LanesShareAnAccountAsync()
    {
        var settings = await _settings.GetAsync();
        return string.Equals(Resolve(settings, ChatGptLanes.Auto).Id,
                             Resolve(settings, ChatGptLanes.Manual).Id,
                             StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Synchronous read for the browser host, which builds its environment before it can await.</summary>
    public string UserDataFolderForLane(string lane)
    {
        var settings = _settings.Current;
        if (settings == null) return LegacyUserDataFolder;
        var account = Resolve(settings, lane);
        return account.UserDataFolder.Length > 0 ? account.UserDataFolder : FolderForId(account.Id);
    }

    // ── writing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an account. Its folder is created empty, so the first navigation lands on a signed-out
    /// ChatGPT and the person signs in once — which is the only way to attach an identity to it.
    /// </summary>
    public async Task<(bool ok, string message, ChatGptAccount? account)> AddAsync(string name)
    {
        var display = (name ?? "").Trim();
        if (display.Length == 0) return (false, "Give the account a name.", null);

        var settings = await _settings.GetForEditAsync();
        var accounts = Ensure(settings);
        if (accounts.Count >= 8)
            return (false, "Eight ChatGPT accounts is already more than this is for.", null);
        if (accounts.Any(a => string.Equals(a.Name, display, StringComparison.OrdinalIgnoreCase)))
            return (false, $"There is already an account called \"{display}\".", null);

        var id = UniqueId(accounts, display);
        var account = new ChatGptAccount { Id = id, Name = display, UserDataFolder = FolderForId(id) };
        accounts.Add(account);
        settings.ChatGptAccounts = accounts;
        await _settings.SaveAsync(settings);

        _activity.Info("ChatGPT", "Account added",
            $"\"{display}\" — open its workspace and sign in once; the session persists after that.");
        Changed?.Invoke();
        return (true, $"Added \"{display}\". Sign in to it once in its workspace.", account.Clone());
    }

    public async Task<(bool ok, string message)> RenameAsync(string accountId, string name)
    {
        var display = (name ?? "").Trim();
        if (display.Length == 0) return (false, "Give the account a name.");

        var settings = await _settings.GetForEditAsync();
        var accounts = Ensure(settings);
        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return (false, "That account is gone.");
        if (accounts.Any(a => a.Id != accountId &&
                              string.Equals(a.Name, display, StringComparison.OrdinalIgnoreCase)))
            return (false, $"There is already an account called \"{display}\".");

        // Name only. The folder is the identity and moving it would sign the account out.
        account.Name = display;
        settings.ChatGptAccounts = accounts;
        await _settings.SaveAsync(settings);
        Changed?.Invoke();
        return (true, $"Renamed to \"{display}\".");
    }

    /// <summary>
    /// Forgets an account. The browser profile on disk is left alone — deleting it would throw away
    /// a signed-in session to undo a naming mistake, and it is the user's to remove if they mean to.
    /// </summary>
    public async Task<(bool ok, string message)> RemoveAsync(string accountId)
    {
        var settings = await _settings.GetForEditAsync();
        var accounts = Ensure(settings);
        if (accounts.Count <= 1) return (false, "There has to be one account to sign in as.");

        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return (false, "That account is gone.");

        accounts.Remove(account);
        // Any lane pointing at it falls back to whatever is left, rather than to a dangling id that
        // would resolve to the default and silently change which identity a lane runs as.
        var fallback = accounts[0].Id;
        var moved = new List<string>();
        foreach (var lane in ChatGptLanes.All)
        {
            if (!settings.ChatGptLaneAccounts.TryGetValue(lane, out var assigned) || assigned != accountId) continue;
            settings.ChatGptLaneAccounts[lane] = fallback;
            moved.Add(ChatGptLanes.Label(lane));
        }
        settings.ChatGptAccounts = accounts;
        await _settings.SaveAsync(settings);

        var note = moved.Count == 0 ? "" : $" {string.Join(" and ", moved)} moved to \"{accounts[0].Name}\".";
        _activity.Info("ChatGPT", "Account removed",
            $"\"{account.Name}\".{note} Its browser profile is still on disk at {account.UserDataFolder}.");
        Changed?.Invoke();
        return (true, $"Removed \"{account.Name}\".{note}");
    }

    /// <summary>Points a lane at an account. Takes effect when that workspace's browser restarts.</summary>
    public async Task<(bool ok, string message)> AssignAsync(string lane, string accountId)
    {
        if (!ChatGptLanes.IsKnown(lane)) return (false, $"\"{lane}\" is not a lane.");

        var settings = await _settings.GetForEditAsync();
        var accounts = Ensure(settings);
        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null) return (false, "That account is gone.");

        settings.ChatGptLaneAccounts[lane] = accountId;
        await _settings.SaveAsync(settings);

        var shared = string.Equals(Resolve(settings, ChatGptLanes.Auto).Id,
                                   Resolve(settings, ChatGptLanes.Manual).Id,
                                   StringComparison.OrdinalIgnoreCase);
        _activity.Info("ChatGPT", $"{ChatGptLanes.Label(lane)} now signs in as \"{account.Name}\"",
            shared
                ? "Both lanes are on one account, so their conversations are kept apart by the app."
                : "The two lanes are on separate accounts and cannot interfere.");
        Changed?.Invoke();
        return (true, $"{ChatGptLanes.Label(lane)} will use \"{account.Name}\" — restart that workspace's browser to apply it.");
    }

    // ── internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// The account list, seeded on first read.
    ///
    /// <para>
    /// The seeded default adopts the folder the single browser was already using, so upgrading does
    /// not sign anybody out. That is the whole reason <see cref="ChatGptAccount.UserDataFolder"/> is
    /// stored rather than derived from the id.
    /// </para>
    /// </summary>
    private static List<ChatGptAccount> Ensure(AppSettings settings)
    {
        var accounts = settings.ChatGptAccounts ??= [];
        if (accounts.Count == 0)
        {
            accounts.Add(new ChatGptAccount
            {
                Id = DefaultAccountId,
                Name = "Default",
                UserDataFolder = LegacyUserDataFolder,
                Note = "The account this app was already signed in to.",
            });
        }
        // A blank folder on a hand-edited settings file would put two accounts in one directory,
        // which is two lanes on one identity while the UI says otherwise.
        foreach (var account in accounts.Where(a => string.IsNullOrWhiteSpace(a.UserDataFolder)))
            account.UserDataFolder = FolderForId(account.Id);

        settings.ChatGptLaneAccounts ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return accounts;
    }

    private static ChatGptAccount Resolve(AppSettings settings, string lane)
    {
        var accounts = Ensure(settings);
        if (settings.ChatGptLaneAccounts.TryGetValue(lane, out var id))
        {
            var assigned = accounts.FirstOrDefault(a => a.Id == id);
            if (assigned != null) return assigned;
        }
        return accounts.FirstOrDefault(a => a.Id == DefaultAccountId) ?? accounts[0];
    }

    private static string UniqueId(List<ChatGptAccount> accounts, string name)
    {
        var slug = NotSlug.Replace(name.ToLowerInvariant().Replace(' ', '-'), "").Trim('-');
        if (slug.Length == 0) slug = "account";
        if (slug.Length > 24) slug = slug[..24];
        if (slug == DefaultAccountId) slug = "account";

        var candidate = slug;
        var suffix = 2;
        while (accounts.Any(a => a.Id == candidate)) candidate = $"{slug}-{suffix++}";
        return candidate;
    }
}
