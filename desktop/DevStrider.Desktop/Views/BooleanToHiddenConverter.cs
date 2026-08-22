using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Maps false to <see cref="Visibility.Hidden"/> rather than <see cref="Visibility.Collapsed"/>,
/// for the WebView-backed workspaces that must keep working while another tab is on screen.
///
/// <para>
/// A collapsed element is arranged at zero size, and the WPF WebView2 control forwards its arranged
/// size straight to the browser as the viewport. A 0x0 viewport gives every element on the page an
/// empty bounding box, so every <c>offsetWidth || getClientRects().length</c> test in the fill,
/// extraction, gate and ChatGPT scripts reports false: the form filler skips every field and reports
/// "filled 0", and the ChatGPT driver never finds its send button. Hidden keeps the real arranged
/// size — so a real viewport and real layout — while still neither painting nor hit-testing.
/// </para>
/// </summary>
public sealed class BooleanToHiddenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
