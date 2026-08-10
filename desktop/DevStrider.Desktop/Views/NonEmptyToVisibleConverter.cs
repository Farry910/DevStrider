using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Collapses an element when the bound string is empty.
///
/// <para>
/// Used for row actions that only make sense once something exists — Open and Remove on an
/// interview's resume. Offering a button whose only possible outcome is "nothing attached" is
/// worse than not showing it.
/// </para>
/// </summary>
public sealed class NonEmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
