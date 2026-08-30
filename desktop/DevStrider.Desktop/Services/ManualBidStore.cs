using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The manual bids list, and the seam the automatic run hands failed links across.
///
/// <para>
/// Manual bids and automatic runs are two tabs doing two different jobs, so they keep two lists.
/// They used to share one queue filtered by intent, which made the handoff a one-line change and
/// everything else awkward: two view-models reading and writing one collection, each having to
/// filter the other's rows out of its own grid.
/// </para>
///
/// <para>
/// This owns the manual list instead. The Job Browser calls <see cref="AddAsync"/> when a posting
/// defeats the automation and never thinks about it again; the Manual Bids tab listens on
/// <see cref="Changed"/> and shows what is there. Neither view-model references the other.
/// </para>
///
/// <para>
/// Stored per profile, beside the automatic queue, in <c>settings.json</c> — the same place and
/// the same shape, so a manual bid survives a restart exactly as a queued link does.
/// </para>
/// </summary>
public sealed class ManualBidStore
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;

    /// <summary>
    /// One writer at a time. The Job Browser adds from a run while the Manual Bids tab is editing
    /// a description, and both read-modify-write the same settings object.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManualBidStore(SettingsService settings, ProfileContext profiles)
    {
        _settings = settings;
        _profiles = profiles;
    }

    /// <summary>Raised after the list changes, whichever tab changed it.</summary>
    public event Action? Changed;

    private string? ProfileKey => _profiles.Current?.Id.ToString();

    /// <summary>Everything on the manual list for the active profile, oldest first.</summary>
    public async Task<List<JobLinkQueueItem>> LoadAsync()
    {
        var key = ProfileKey;
        if (key == null) return [];
        var settings = await _settings.GetAsync();
        return settings.ManualBidQueues.TryGetValue(key, out var saved)
            ? saved.Select(item => item.Clone()).ToList()
            : [];
    }

    /// <summary>
    /// Puts a link on the manual list, keeping whatever it already carries.
    ///
    /// <para>
    /// Most links arrive here from a run that failed at opening the form — which happens after the
    /// description was read — so they come with a job description already on them and a resume one
    /// button away. Nothing is cleared on the way in for that reason.
    /// </para>
    /// </summary>
    public async Task<bool> AddAsync(JobLinkQueueItem item)
    {
        var key = ProfileKey;
        if (key == null) return false;

        await _gate.WaitAsync();
        try
        {
            var settings = await _settings.GetForEditAsync();
            var list = settings.ManualBidQueues.TryGetValue(key, out var saved) ? saved : [];
            // Same posting twice is one row. A link that failed, was worked by hand, and failed
            // again on a retry should not appear twice with two descriptions.
            if (list.Any(existing => UrlNorm.Normalize(existing.Url) == UrlNorm.Normalize(item.Url)))
                return false;

            var copy = item.Clone();
            copy.Intent = JobWorkItemIntents.Manual;
            copy.Status = JobLinkQueueStatuses.ManualBid;
            list.Add(copy);
            settings.ManualBidQueues[key] = list;
            await _settings.SaveAsync(settings);
        }
        finally { _gate.Release(); }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Writes the list back. The Manual Bids tab owns the rows and saves the whole set.</summary>
    public async Task SaveAsync(IEnumerable<JobLinkQueueItem> items)
    {
        var key = ProfileKey;
        if (key == null) return;

        await _gate.WaitAsync();
        try
        {
            var settings = await _settings.GetForEditAsync();
            settings.ManualBidQueues[key] = items.Select(item => item.Clone()).ToList();
            await _settings.SaveAsync(settings);
        }
        finally { _gate.Release(); }
    }

    /// <summary>How many are waiting, for the nav badge. Cheap enough to call on every change.</summary>
    public int CountFor(AppSettings settings)
    {
        var key = ProfileKey;
        return key != null && settings.ManualBidQueues.TryGetValue(key, out var saved) ? saved.Count : 0;
    }
}
