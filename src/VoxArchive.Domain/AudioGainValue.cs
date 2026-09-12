using System.Globalization;

namespace VoxArchive.Domain;

/// <summary>
/// Audio EditorのGain値について、-∞終端と数値入力の共通規則を提供する。
/// </summary>
public static class AudioGainValue
{
    public const double MinimumFiniteDb = -60d;
    public const double MaximumDb = 20d;

    /// <summary>
    /// Sliderの最小端を-∞dBとして解釈する。
    /// </summary>
    public static double FromSlider(double sliderDb)
        => sliderDb <= MinimumFiniteDb ? double.NegativeInfinity : Math.Clamp(sliderDb, MinimumFiniteDb, MaximumDb);

    /// <summary>
    /// -∞dBをSliderの最小端へ投影する。
    /// </summary>
    public static double ToSlider(double gainDb)
        => double.IsNegativeInfinity(gainDb) ? MinimumFiniteDb : Math.Clamp(gainDb, MinimumFiniteDb, MaximumDb);

    /// <summary>
    /// UI表示用にGainを整形する。
    /// </summary>
    public static string Format(double gainDb, IFormatProvider? provider = null)
        => double.IsNegativeInfinity(gainDb)
            ? "-∞"
            : gainDb.ToString("F1", provider ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// 数値または-∞表記をGain値として解釈する。
    /// </summary>
    public static bool TryParse(string? text, IFormatProvider? provider, out double gainDb)
    {
        gainDb = 0d;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = text.Trim();
        if (normalized.Equals("-∞", StringComparison.Ordinal)
            || normalized.Equals("-inf", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("-infinity", StringComparison.OrdinalIgnoreCase))
        {
            gainDb = double.NegativeInfinity;
            return true;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, provider ?? CultureInfo.CurrentCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            return false;
        }

        gainDb = Math.Round(Math.Clamp(parsed, MinimumFiniteDb, MaximumDb), 1, MidpointRounding.AwayFromZero);
        return true;
    }
}
