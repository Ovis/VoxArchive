using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// ReazonSpeech固有のprecision、decoding、beam、CPU thread設定を編集するControlを提供する
/// </summary>
/// <remarks>
/// このControlは保存処理を持たず、親設定Windowの編集バッファだけを操作する。
/// 「既定値に戻す」も即時永続化せず、設定Windowの保存時にのみ反映される。
/// </remarks>
public partial class ReazonSpeechAdvancedSettingsControl : UserControl
{
    private const string DefaultPrecision = "int8-fp32";
    private const string DefaultDecodingMethod = "greedy_search";
    private const int DefaultMaxActivePaths = 4;

    /// <summary>Controlを初期化する</summary>
    public ReazonSpeechAdvancedSettingsControl()
    {
        InitializeComponent();
        CpuThreadsControl.Maximum = Environment.ProcessorCount;
        ApplyValues(new Dictionary<string, string>
        {
            ["precision"] = DefaultPrecision,
            ["decodingMethod"] = DefaultDecodingMethod,
            ["maxActivePaths"] = DefaultMaxActivePaths.ToString(),
            ["cpuThreads"] = Math.Min(4, Environment.ProcessorCount).ToString()
        });
    }

    /// <summary>現在の編集値を安定ID/valueとして取得する</summary>
    public IReadOnlyDictionary<string, string> GetValues()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["precision"] = GetSelectedTag(PrecisionComboBox, DefaultPrecision),
            ["decodingMethod"] = GetSelectedTag(DecodingMethodComboBox, DefaultDecodingMethod),
            ["maxActivePaths"] = checked((int)MaxActivePathsControl.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cpuThreads"] = checked((int)CpuThreadsControl.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    /// <summary>Application層から取得した安定ID/valueを編集UIへ反映する</summary>
    /// <param name="values">ReazonSpeech advanced settings capabilityが公開した値</param>
    public void ApplyValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        SelectByTag(PrecisionComboBox, GetValue(values, "precision", DefaultPrecision));
        SelectByTag(DecodingMethodComboBox, GetValue(values, "decodingMethod", DefaultDecodingMethod));
        MaxActivePathsControl.Value = ParseRequiredInt(values, "maxActivePaths", DefaultMaxActivePaths, 1, int.MaxValue);
        CpuThreadsControl.Value = ParseRequiredInt(
            values,
            "cpuThreads",
            Math.Min(4, Environment.ProcessorCount),
            1,
            Environment.ProcessorCount);
        UpdateDecodingDependentState();
    }

    private void OnDecodingMethodSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDecodingDependentState();

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        ApplyValues(new Dictionary<string, string>
        {
            ["precision"] = DefaultPrecision,
            ["decodingMethod"] = DefaultDecodingMethod,
            ["maxActivePaths"] = DefaultMaxActivePaths.ToString(),
            ["cpuThreads"] = Math.Min(4, Environment.ProcessorCount).ToString()
        });
    }

    private void UpdateDecodingDependentState()
    {
        // max_active_pathsはmodified beam searchでのみ意味を持つため、greedy時は編集不可にする。
        // 値自体は保持しておき、decoding方式を往復しても利用者の編集値を失わない。
        MaxActivePathsPanel.IsEnabled = string.Equals(
            GetSelectedTag(DecodingMethodComboBox, DefaultDecodingMethod),
            "modified_beam_search",
            StringComparison.Ordinal);
    }

    private static int ParseRequiredInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        var raw = GetValue(values, key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(key, raw, $"{key}は{minimum}～{maximum}の範囲である必要があります。");
        }

        return parsed;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static string GetSelectedTag(ComboBox comboBox, string fallback)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        throw new ArgumentException($"未対応の設定値です: {value}", nameof(value));
    }
}
