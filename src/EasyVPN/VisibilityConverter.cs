using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EasyVPN;

/// <summary>
/// Shows an element for <c>true</c>, a non-empty string or collection, or any other non-null value.
/// <c>ConverterParameter=invert</c> turns that around.
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public static readonly VisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var shown = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert") shown = !shown;
        return shown ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
