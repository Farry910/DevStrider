namespace DevStrider.Desktop.Services;

/// <summary>
/// The command line both embedded browsers are created with.
///
/// <para>
/// Chromium assumes it is a browser somebody is looking at. When its window is covered, minimised or
/// on another virtual desktop it treats the page as unwatched and turns it down: timers are throttled
/// to about once a second, animation frames stop entirely, and renderer priority drops. Measured on
/// this app with nothing more than a terminal in front of it, a 100ms interval was firing 0.3 times
/// a second and requestAnimationFrame was not firing at all.
/// </para>
///
/// <para>
/// For a browser that is correct — nobody is watching, so nobody should pay for it. For this app it
/// is fatal, because the "unwatched" page is the one being driven: a job form being filled, or a
/// ChatGPT reply being waited for. A run left to work in the background did not run slowly, it
/// stopped, and the only way to keep it going was to leave the window on top of everything else.
/// </para>
///
/// <para>
/// These four switches turn that behaviour off. They have to be passed when the environment is
/// created — a Chromium command line cannot be changed on a browser that is already running, which
/// is the same reason the proxy asks for a restart.
/// </para>
/// </summary>
public static class BrowserLaunch
{
    /// <summary>
    /// Keeps a page running at full speed while nothing is looking at it.
    ///
    /// <list type="bullet">
    /// <item><c>CalculateNativeWinOcclusion</c> is the Windows-specific check that decides a window
    /// is covered by another one. It is what makes "works only when on top" the rule, and disabling
    /// the feature is what makes a covered window behave like an uncovered one.</item>
    /// <item><c>backgrounding-occluded-windows</c> is the action taken once that check fires.</item>
    /// <item><c>renderer-backgrounding</c> lowers the renderer process priority.</item>
    /// <item><c>background-timer-throttling</c> is the one that stretches setTimeout and setInterval
    /// out to about a second — the reason a reply-wait or a settle delay quietly takes minutes.</item>
    /// </list>
    /// </summary>
    private const string StayAwake =
        "--disable-features=CalculateNativeWinOcclusion " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding " +
        "--disable-background-timer-throttling";

    /// <summary>
    /// The full argument string for one browser: the background switches, plus the proxy when it
    /// applies to this browser.
    /// </summary>
    public static string Arguments(ProxyConfiguration proxy, bool forChatGpt)
    {
        var proxyArguments = proxy.BrowserArguments(forChatGpt);
        return proxyArguments.Length == 0 ? StayAwake : StayAwake + " " + proxyArguments;
    }
}
