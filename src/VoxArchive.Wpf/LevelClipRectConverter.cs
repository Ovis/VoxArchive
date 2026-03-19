using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VoxArchive.Wpf;

public sealed class LevelClipRectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3)
        {
            return Rect.Empty;
        }

        var levelPercent = ToDouble(values[0]);
        var width = ToDouble(values[1]);
        var height = ToDouble(values[2]);

        if (width <= 0 || height <= 0)
        {
            return Rect.Empty;
        }

        var ratio = Math.Clamp(levelPercent / 100.0, 0.0, 1.0);
        var isVertical = string.Equals(parameter?.ToString(), "Vertical", StringComparison.OrdinalIgnoreCase);

        if (isVertical)
        {
            var clipHeight = height * ratio;
            return new Rect(0, height - clipHeight, width, clipHeight);
        }

        return new Rect(0, 0, width * ratio, height);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static double ToDouble(object value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => 0.0
        };
    }
}

