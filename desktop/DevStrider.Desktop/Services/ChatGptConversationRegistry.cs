namespace DevStrider.Desktop.Services;

/// <summary>
/// Who owns which ChatGPT conversation, so two lanes on one account never end up in the same chat.
///
/// <para>
/// A conversation is not a shared resource that tolerates two writers. Every resume after the first
/// in a chat depends on the profile prompt sent at the top of it, and the app continues a chat by
/// sending a bare job description into it. Two lanes continuing the same <c>/c/…</c> would
/// interleave two jobs' descriptions and two jobs' replies into one thread, and each would read the
/// other's answer as its own — a plausible resume, for the wrong posting, with nothing about it
/// looking wrong.
/// </para>
///
/// <para>
/// The remembered conversation is stored per profile <em>and per lane</em>
/// (<see cref="SessionKey"/>), which is what keeps them apart in the ordinary case. This registry
/// is the backstop for the case that key cannot cover: the same id arriving in both lanes anyway,
/// because ChatGPT was resumed from its own sidebar, or a settings file was hand-edited, or the two
/// lanes were pointed at one account after the fact. First claim wins; the loser starts a fresh
/// chat, which costs one profile prompt and is always safe.
/// </para>
///
/// <para>
/// Scoped by account id, because two different signed-in users have entirely separate conversation
/// lists and the same id string under each is two unrelated chats. Lanes on different accounts
/// therefore never contend, and <see cref="ChatGptAccountService.LanesShareAnAccountAsync"/> is how
/// the UI knows whether any of this is load-bearing.
/// </para>
/// </summary>
public sealed class ChatGptConversationRegistry
{
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;
    private readonly BidTraceService _trace;

    /// <summary>
    /// Serializes claim and release. Both read-modify-write the settings, and a manual bid starting
    /// while an automatic run rotates its chat is exactly when two of them land together.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChatGptConversationRegistry(SettingsService settings, ActivityLogService activity,
        BidTraceService trace)
    {
        _settings = settings;
        _activity = activity;
        _trace = trace;
    }

    /// <summary>
    /// The per-profile, per-lane key the remembered conversation is stored under.
    ///
    /// <para>
    /// Before lanes existed this was the profile id alone, which is the collision in its simplest
    /// form: both lanes read the same remembered URL at startup and both continued that one chat.
    /// A settings file written by an older build still deserializes — its bare-profile-id entries
    /// simply stop being read, and each lane starts a fresh chat once.
    /// </para>
    /// </summary>
    public static string SessionKey(string profileId, string lane) => $"{profileId}|{lane}";

    /// <summary>
    /// The map key for one conversation on one account. Scoped by account because two signed-in
    /// users have entirely separate chat lists, so the same id string under each is two unrelated
    /// conversations and must not collide.
    /// </summary>
    public static string Slot(string accountId, string conversationId) =>
        $"{accountId}|{conversationId}";

    /// <summary>
    /// The whole arbitration rule, as a function of the map rather than of the app's state.
    ///
    /// <para>
    /// Pulled out so it can be exercised directly: the surrounding class reads and writes settings
    /// through a store that resolves a real per-user directory, which makes the decision itself the
    /// only part that can be tested without writing to somebody's machine. Both
    /// <see cref="MayUse"/> and <see cref="TryClaimAsync"/> route through here so there is one rule
    /// and not two that have to agree.
    /// </para>
    /// </summary>
    /// <returns>True when the lane may use the conversation: unclaimed, or already its own.</returns>
    public static bool Allows(IReadOnlyDictionary<string, string>? owners,
        string accountId, string conversationId, string lane)
    {
        // Nothing to arbitrate: a conversation with no id has not been created yet, which is the
        // normal state right up until ChatGPT answers for the first time.
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(accountId)) return true;
        if (owners == null) return true;
        return !owners.TryGetValue(Slot(accountId, conversationId), out var holder)
               || string.Equals(holder, lane, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drops this lane's claims on this account other than <paramref name="keep"/>.
    ///
    /// <para>
    /// A lane drives one conversation at a time — rotating to a fresh chat abandons the previous
    /// one — so keeping the old ids would grow the map for the life of the install and, worse, hold
    /// the other lane off chats abandoned weeks ago.
    /// </para>
    ///
    /// <para>
    /// Compares the whole slot key rather than a <c>EndsWith("|" + keep)</c> suffix, which would
    /// spare an unrelated claim whenever one conversation id happens to end with another — keeping
    /// <c>abc</c> would also keep <c>xyzabc</c>.
    /// </para>
    /// </summary>
    /// <returns>How many claims were dropped.</returns>
    public static int PruneLane(IDictionary<string, string> owners, string accountId, string lane, string keep)
    {
        var prefix = accountId + "|";
        var keepSlot = Slot(accountId, keep);
        var stale = owners
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(pair.Value, lane, StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(pair.Key, keepSlot, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key).ToList();
        foreach (var key in stale) owners.Remove(key);
        return stale.Count;
    }

    /// <summary>
    /// Whether this lane may use a conversation, decided from settings already in memory.
    ///
    /// <para>
    /// The decision is needed on the UI thread, inside the synchronous method that builds a resume
    /// request, and blocking there on <see cref="TryClaimAsync"/> would mean waiting on a chain
    /// that saves to disk — a deadlock waiting for a slow write. So the <em>read</em> is
    /// synchronous and lock-free (the settings instance is already loaded and the owners map is
    /// only ever replaced wholesale), and the caller follows up with the asynchronous claim to
    /// record it. A claim racing in between costs a fresh chat, never a shared one: the loser of
    /// the race reads the winner's entry on its next pass.
    /// </para>
    /// </summary>
    public bool MayUse(string accountId, string conversationId, string lane) =>
        Allows(_settings.Current?.ChatGptConversationOwners, accountId, conversationId, lane);

    /// <summary>
    /// Asks whether <paramref name="lane"/> may use this conversation, and claims it if so.
    ///
    /// <para>
    /// Returns false only when another lane holds it. The caller's response to false is always the
    /// same — start a fresh chat — so this deliberately does not distinguish the reasons it might
    /// be held.
    /// </para>
    /// </summary>
    public async Task<bool> TryClaimAsync(string accountId, string conversationId, string lane)
    {
        // Nothing to arbitrate over. A conversation with no id has not been created yet, which is
        // the normal state right up until ChatGPT answers for the first time.
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(accountId)) return true;

        await _gate.WaitAsync();
        try
        {
            var settings = await _settings.GetForEditAsync();
            var owners = settings.ChatGptConversationOwners ??= new(StringComparer.OrdinalIgnoreCase);
            var slot = Slot(accountId, conversationId);

            if (!Allows(owners, accountId, conversationId, lane))
            {
                var holder = owners[slot];
                _trace.Warn("ChatGPT", "conversation already claimed",
                    $"{conversationId} on account {accountId} is held by {ChatGptLanes.Label(holder)}; "
                    + $"{ChatGptLanes.Label(lane)} will start a fresh chat instead.");
                _activity.Info("ChatGPT", "Started a separate chat",
                    $"{ChatGptLanes.Label(lane)} and {ChatGptLanes.Label(holder)} are on one account, so "
                    + "they were kept out of each other's conversation.");
                return false;
            }

            owners[slot] = lane;
            PruneLane(owners, accountId, lane, keep: conversationId);
            await _settings.SaveAsync(settings);
            _trace.Step("ChatGPT", "conversation claimed", $"{conversationId} → {ChatGptLanes.Label(lane)}");
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Gives up whatever this lane holds on an account. Called when a lane rotates to a fresh chat
    /// or its workspace shuts down, so an abandoned id does not sit claimed for ever and push the
    /// other lane off a conversation nobody is using.
    /// </summary>
    public async Task ReleaseLaneAsync(string accountId, string lane)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;

        await _gate.WaitAsync();
        try
        {
            var settings = await _settings.GetForEditAsync();
            var owners = settings.ChatGptConversationOwners;
            if (owners == null || owners.Count == 0) return;

            var prefix = accountId + "|";
            var mine = owners
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(pair.Value, lane, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToList();
            if (mine.Count == 0) return;

            foreach (var key in mine) owners.Remove(key);
            await _settings.SaveAsync(settings);
            _trace.Step("ChatGPT", "conversation released", $"{mine.Count} claim(s) for {ChatGptLanes.Label(lane)}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Which lane holds a conversation, or empty. For the management centre's read-out.</summary>
    public async Task<string> OwnerOfAsync(string accountId, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return "";
        var settings = await _settings.GetAsync();
        var owners = settings.ChatGptConversationOwners;
        return owners != null && owners.TryGetValue(Slot(accountId, conversationId), out var holder) ? holder : "";
    }

    /// <summary>Everything currently claimed, newest-irrelevant, for the management centre.</summary>
    public async Task<List<(string AccountId, string ConversationId, string Lane)>> ListClaimsAsync()
    {
        var settings = await _settings.GetAsync();
        var owners = settings.ChatGptConversationOwners;
        if (owners == null) return [];
        return owners
            .Select(pair =>
            {
                var split = pair.Key.Split('|', 2);
                return (AccountId: split[0], ConversationId: split.Length > 1 ? split[1] : "", Lane: pair.Value);
            })
            .Where(claim => claim.ConversationId.Length > 0)
            .ToList();
    }

}
