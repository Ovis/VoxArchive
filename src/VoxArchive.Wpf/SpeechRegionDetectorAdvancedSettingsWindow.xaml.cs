using System.Windows;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Silero VADの詳細設定を編集バッファとして扱うダイアログを提供する
/// </summary>
public partial class SpeechRegionDetectorAdvancedSettingsWindow : Window
{
    /// <summary>
    /// 現在設定を初期値としてダイアログを生成する
    /// </summary>
    public SpeechRegionDetectorAdvancedSettingsWindow(SileroVadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        ConfigureNumericControls();
        ApplySettings(settings);
        ResultSettings = settings;
    }

    /// <summary>OK確定後の編集結果を取得する</summary>
    public SileroVadSettings ResultSettings { get; private set; }

    private void ConfigureNumericControls()
    {
        ThresholdNumericUpDown.Minimum = 0.01d;
        ThresholdNumericUpDown.Maximum = 0.99d;
        ThresholdNumericUpDown.Increment = 0.01d;
        ThresholdNumericUpDown.DecimalPlaces = 2;

        ConfigureMilliseconds(MinimumSpeechNumericUpDown, minimum: 1d);
        ConfigureMilliseconds(MinimumSilenceNumericUpDown, minimum: 1d);
        ConfigureMilliseconds(PrePaddingNumericUpDown, minimum: 0d);
        ConfigureMilliseconds(PostPaddingNumericUpDown, minimum: 0d);
    }

    private static void ConfigureMilliseconds(NumericUpDownControl control, double minimum)
    {
        control.Minimum = minimum;
        control.Maximum = int.MaxValue;
        control.Increment = 10d;
        control.DecimalPlaces = 0;
        control.UnitText = "ms";
    }

    private void ApplySettings(SileroVadSettings settings)
    {
        ThresholdNumericUpDown.Value = settings.Threshold;
        MinimumSpeechNumericUpDown.Value = settings.MinimumSpeechDurationMilliseconds;
        MinimumSilenceNumericUpDown.Value = settings.MinimumSilenceDurationMilliseconds;
        PrePaddingNumericUpDown.Value = settings.PrePaddingMilliseconds;
        PostPaddingNumericUpDown.Value = settings.PostPaddingMilliseconds;
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        // 親SettingsWindowが保存されるまでは永続化しない。ここでは編集バッファだけを標準値へ戻す。
        ApplySettings(new SileroVadSettings());
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        ResultSettings = new SileroVadSettings
        {
            Threshold = ThresholdNumericUpDown.Value,
            MinimumSpeechDurationMilliseconds = checked((int)MinimumSpeechNumericUpDown.Value),
            MinimumSilenceDurationMilliseconds = checked((int)MinimumSilenceNumericUpDown.Value),
            PrePaddingMilliseconds = checked((int)PrePaddingNumericUpDown.Value),
            PostPaddingMilliseconds = checked((int)PostPaddingNumericUpDown.Value)
        };
        DialogResult = true;
    }
}
