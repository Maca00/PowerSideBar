using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerSideBar.Converters;

/// <summary>
/// Returns Visible when the string is null or empty, Collapsed otherwise.
/// </summary>
public class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
