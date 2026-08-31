using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Parks a workspace off the side of the window instead of hiding it.
///
/// <para>
/// A WPF <c>Visibility="Hidden"</c> on a view containing a WebView2 stops that browser rendering:
/// <c>document.visibilityState</c> becomes "hidden" and <c>requestAnimationFrame</c> stops firing
/// entirely — measured at 0 frames over six seconds against 394 for a visible one. Timers, DOM
/// writes and layout all keep working, which is why hiding looked safe.
/// </para>
///
/// <para>
/// It is not safe for ChatGPT. Its reply is streamed over the network and then <em>rendered</em>,
/// and that render commits inside an animation frame. With no frames the text never reaches the
/// DOM, so a run waiting on <c>innerText</c> sees an empty reply and times out three minutes
/// later — "a reply arrived (0 chars)". The page is not slow; it is stopped.
/// </para>
///
/// <para>
/// So the inactive workspace stays <c>Visible</c> and is moved out of view by a large negative
/// margin instead. WebView2 still considers it visible and keeps painting; the window clips it, so
/// nobody sees it. Being covered would ordinarily invite Chromium's occlusion throttling, and that
/// is already disabled — see <see cref="Services.BrowserLaunch"/>.
/// </para>
/// </summary>
public sealed class OffscreenWhenInactiveConverter : IValueConverter
{
    /// <summary>
    /// Far enough to clear any monitor the window could be on, and no further: the value is a
    /// layout offset, and a preposterous one costs measure/arrange precision for nothing.
    /// </summary>
    private const double Offset = 20000;

    private static readonly Thickness Onscreen = new(0);
    private static readonly Thickness Parked = new(-Offset, 0, Offset, 0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Onscreen : Parked;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way: layout follows the selected workspace.");
}
