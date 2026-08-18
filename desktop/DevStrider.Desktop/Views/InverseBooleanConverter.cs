using System.Globalization;
using System.Windows.Data;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Negates a bool — for binding <c>IsEnabled</c> to a busy flag.
///
/// <para>
/// The alternative is a second property on every view-model that exists only to say "not busy",
/// which drifts out of sync the first time someone forgets to raise it.
/// </para>
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}
