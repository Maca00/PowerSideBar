using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PowerSideBar.Converters;

/// <summary>
/// Maps temperature (°C) to text color — same scale for max and min.
/// ≤14 blue, 15–17 light blue, 18–22 white, 23–26 yellow, ≥27 orange.
/// </summary>
public class TempToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Cold = Create("#74bbea");
    private static readonly SolidColorBrush Cool = Create("#a9d4ef");
    private static readonly SolidColorBrush Mild = Create("#FFFFFF");
    private static readonly SolidColorBrush Warm = Create("#f2c66b");
    private static readonly SolidColorBrush Hot  = Create("#ef7e54");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var temp = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
            string s when double.TryParse(s.TrimEnd('°'), NumberStyles.Any, culture, out var p2) => p2,
            _ => 0d,
        };

        if (temp <= 14) return Cold;
        if (temp <= 17) return Cool;
        if (temp <= 22) return Mild;
        if (temp <= 28) return Warm;
        return Hot;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Create(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        brush.Freeze();
        return brush;
    }
}
