using System.Globalization;
using System.Windows.Data;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こし区間の秒数を、長時間録音でも桁が崩れない時刻表記へ変換する
/// </summary>
public sealed class TranscriptionTimeConverter : IValueConverter
{
    /// <summary>
    /// 秒数を h:mm:ss 形式へ変換する
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double seconds || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "0:00:00";
        }

        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
