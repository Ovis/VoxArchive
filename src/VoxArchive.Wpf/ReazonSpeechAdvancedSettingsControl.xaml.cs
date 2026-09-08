using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// ReazonSpeech固有のprecision、decoding、beam、CPU thread設定を編集するControlを提供する
/// </summary>
/// <remarks>
/// このControlは保存処理を持たず、親設定Windowの編集バッファだけを操作する。
/// 「既定値に戻す」も即時永続化せず、設定Windowの保存時にのみ反映される。
/// 保存済み値が現在の環境で無効でも読み込み自体は失敗させず、その値を表示したまま利用者が修正できる状態を維持する。
/// 新たなユーザー入力では実行可能範囲だけを許可し、既存の不正保存値を保持する互換性と入力制約を分離する。
/// </remarks>
public partial class ReazonSpeechAdvancedSettingsControl : UserControl
{
    private const string DefaultPrecision = "int8-fp32";
    private const string DefaultDecodingMethod = "greedy_search";
    private const int DefaultMaxActivePaths = 4;
    private static readonly string[] SupportedPrecisions = ["fp32", "int8", "int8-fp32"];
    private static readonly string[] SupportedDecodingMethods = ["greedy_search", "modified_beam_search"];
    private bool _isApplyingValues;

    /// <summary>モデル管理対象packageが変わるprecision変更を通知する</summary>
    public event EventHandler? PrecisionChanged;

    /// <summary>Controlを初期化する</summary>
    public ReazonSpeechAdvancedSettingsControl()
    {
        InitializeComponent();
        ConfigureNumericControls();
        PrecisionComboBox.SelectionChanged += OnPrecisionSelectionChanged;
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

        _isApplyingValues = true;
        try
        {
            var precision = GetValue(values, "precision", DefaultPrecision);
            var decodingMethod = GetValue(values, "decodingMethod", DefaultDecodingMethod);
            var maxActivePaths = ParseInt(values, "maxActivePaths", DefaultMaxActivePaths);
            var cpuThreads = ParseInt(values, "cpuThreads", Math.Min(4, Environment.ProcessorCount));

            SelectByTagAllowingUnsupported(PrecisionComboBox, precision, SupportedPrecisions);
            SelectByTagAllowingUnsupported(DecodingMethodComboBox, decodingMethod, SupportedDecodingMethods);

            // 旧バージョン等から不正な保存値を読み込んだ場合は、その値を勝手にclampせず表示・保持する。
            // ただしControlの通常入力範囲は直後に実行可能値へ戻し、新たな不正値をユーザーが入力できないようにする。
            ApplyPersistedNumericValue(MaxActivePathsControl, maxActivePaths, 1, int.MaxValue);
            ApplyPersistedNumericValue(CpuThreadsControl, cpuThreads, 1, Math.Max(1, Environment.ProcessorCount));

            UpdateDecodingDependentState();
            UpdateValidationMessage(precision, decodingMethod, maxActivePaths, cpuThreads);
        }
        finally
        {
            _isApplyingValues = false;
        }
    }

    private void ConfigureNumericControls()
    {
        MaxActivePathsControl.Minimum = 1;
        MaxActivePathsControl.Maximum = int.MaxValue;
        MaxActivePathsControl.Increment = 1;
        MaxActivePathsControl.DecimalPlaces = 0;

        CpuThreadsControl.Minimum = 1;
        CpuThreadsControl.Maximum = Math.Max(1, Environment.ProcessorCount);
        CpuThreadsControl.Increment = 1;
        CpuThreadsControl.DecimalPlaces = 0;
    }

    private static void ApplyPersistedNumericValue(
        NumericUpDownControl control,
        int value,
        int validMinimum,
        int validMaximum)
    {
        // Value setterは現在の範囲外を拒否するため、保存済み値を復元する瞬間だけ全int範囲を許可する。
        // 復元後は通常編集範囲へ戻すがValue自体は変更しないため、未知・不正な既存値を暗黙修正しない。
        control.Minimum = int.MinValue;
        control.Maximum = int.MaxValue;
        control.Value = value;
        control.Minimum = validMinimum;
        control.Maximum = validMaximum;
    }

    private void OnPrecisionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingValues)
        {
            RemoveUnsupportedPlaceholder(PrecisionComboBox, SupportedPrecisions);
            UpdateValidationMessageFromControls();
            PrecisionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDecodingMethodSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDecodingDependentState();
        if (!_isApplyingValues)
        {
            RemoveUnsupportedPlaceholder(DecodingMethodComboBox, SupportedDecodingMethods);
            UpdateValidationMessageFromControls();
        }
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        var previousPrecision = GetSelectedTag(PrecisionComboBox, DefaultPrecision);
        ApplyValues(new Dictionary<string, string>
        {
            ["precision"] = DefaultPrecision,
            ["decodingMethod"] = DefaultDecodingMethod,
            ["maxActivePaths"] = DefaultMaxActivePaths.ToString(),
            ["cpuThreads"] = Math.Min(4, Environment.ProcessorCount).ToString()
        });

        if (!string.Equals(previousPrecision, DefaultPrecision, StringComparison.Ordinal))
        {
            PrecisionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateDecodingDependentState()
    {
        // max_active_pathsはmodified beam searchでのみ意味を持つため、greedy時は編集不可にする。
        // 未知のdecoding値は現在バージョンで解釈できないため、値を保持したまま編集不可として利用者に修正を促す。
        MaxActivePathsPanel.IsEnabled = string.Equals(
            GetSelectedTag(DecodingMethodComboBox, DefaultDecodingMethod),
            "modified_beam_search",
            StringComparison.Ordinal);
    }

    private void UpdateValidationMessageFromControls()
    {
        UpdateValidationMessage(
            GetSelectedTag(PrecisionComboBox, DefaultPrecision),
            GetSelectedTag(DecodingMethodComboBox, DefaultDecodingMethod),
            checked((int)MaxActivePathsControl.Value),
            checked((int)CpuThreadsControl.Value));
    }

    private void UpdateValidationMessage(string precision, string decodingMethod, int maxActivePaths, int cpuThreads)
    {
        var messages = new List<string>();
        if (!SupportedPrecisions.Contains(precision, StringComparer.OrdinalIgnoreCase))
        {
            messages.Add($"保存済みモデル精度 '{precision}' は現在のバージョンでは無効です。対応値を選び直してください。");
        }
        if (!SupportedDecodingMethods.Contains(decodingMethod, StringComparer.OrdinalIgnoreCase))
        {
            messages.Add($"保存済みDecoding method '{decodingMethod}' は現在のバージョンでは無効です。対応値を選び直してください。");
        }
        if (cpuThreads < 1 || cpuThreads > Environment.ProcessorCount)
        {
            messages.Add($"保存済みCPU threads={cpuThreads} は現在の環境では無効です。1～{Environment.ProcessorCount}へ修正してください。");
        }
        if (string.Equals(decodingMethod, "modified_beam_search", StringComparison.OrdinalIgnoreCase)
            && maxActivePaths < 1)
        {
            messages.Add($"保存済みMax active paths={maxActivePaths} は現在の設定では無効です。1以上へ修正してください。");
        }

        ValidationMessageTextBlock.Text = string.Join(Environment.NewLine, messages);
        ValidationMessageTextBlock.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        var raw = GetValue(values, key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{key}を整数として読み込めません: {raw}", key);
        }
        return parsed;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static string GetSelectedTag(ComboBox comboBox, string fallback)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTagAllowingUnsupported(ComboBox comboBox, string value, IReadOnlyCollection<string> supportedValues)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        // 未知値を既定値へ置換すると保存値が失われるため、現在未対応であることを明示する一時項目として表示する。
        // 利用者が対応値を選択した時点でplaceholderは除去し、以後は新しい値を編集バッファの正本とする。
        var placeholder = new ComboBoxItem
        {
            Content = $"{value}（現在未対応）",
            Tag = value
        };
        comboBox.Items.Add(placeholder);
        comboBox.SelectedItem = placeholder;
    }

    private static void RemoveUnsupportedPlaceholder(ComboBox comboBox, IReadOnlyCollection<string> supportedValues)
    {
        var removable = comboBox.Items
            .OfType<ComboBoxItem>()
            .Where(item => item.Tag is string tag
                           && !supportedValues.Contains(tag, StringComparer.OrdinalIgnoreCase)
                           && !ReferenceEquals(item, comboBox.SelectedItem))
            .ToArray();
        foreach (var item in removable)
        {
            comboBox.Items.Remove(item);
        }
    }
}
