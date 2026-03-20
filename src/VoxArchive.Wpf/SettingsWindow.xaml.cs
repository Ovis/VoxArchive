using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public partial class SettingsWindow : Window
{
    private readonly WhisperModelStore _whisperModelStore;
    private readonly WhisperTranscriptionService _whisperTranscriptionService;

    private static readonly Brush StatusDefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9BB4D1"));
    private static readonly Brush StatusWarningBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCA80"));
    private static readonly Brush StatusErrorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9A9A"));

    private bool _isCapturingHotkey;
    private bool _suppressEnvironmentAutoCheck = true;
    private string _capturedHotkeyText = string.Empty;
    private int _environmentCheckVersion;
    private int _environmentCheckInProgress;

    public SettingsWindow()
        : this(new WhisperModelStore())
    {
    }

    private SettingsWindow(WhisperModelStore whisperModelStore)
        : this(whisperModelStore, new WhisperTranscriptionService(whisperModelStore))
    {
    }

    public SettingsWindow(WhisperModelStore whisperModelStore, WhisperTranscriptionService whisperTranscriptionService)
    {
        _whisperModelStore = whisperModelStore;
        _whisperTranscriptionService = whisperTranscriptionService;

        _suppressEnvironmentAutoCheck = true;
        InitializeComponent();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        ModelDirectoryTextBox.Text = _whisperModelStore.ModelsDirectory;

        TranscriptionExecutionMode = TranscriptionExecutionMode.Auto;
        TranscriptionModel = TranscriptionModel.Small;
        AutoTranscriptionPriority = TranscriptionPriority.Low;
        ManualTranscriptionPriority = TranscriptionPriority.Normal;
        TranscriptionLanguage = "ja";
        OutputTxtCheckBox.IsChecked = true;

        SetDefaultEnvironmentStatus();

        _suppressEnvironmentAutoCheck = false;
    }

    public int AlignmentMilliseconds
    {
        get => int.TryParse(OffsetTextBox.Text, out var ms) ? ms : 0;
        set => OffsetTextBox.Text = value.ToString();
    }

    public double DefaultSpeakerPlaybackGainDb
    {
        get => ParseDouble(DefaultSpeakerGainTextBox.Text);
        set => DefaultSpeakerGainTextBox.Text = value.ToString("F1", CultureInfo.CurrentCulture);
    }

    public double DefaultMicPlaybackGainDb
    {
        get => ParseDouble(DefaultMicGainTextBox.Text);
        set => DefaultMicGainTextBox.Text = value.ToString("F1", CultureInfo.CurrentCulture);
    }

    public string StartStopHotkeyText
    {
        get => StartStopHotkeyTextBox.Text.Trim();
        set
        {
            StartStopHotkeyTextBox.Text = value;
            _capturedHotkeyText = value;
        }
    }

    public string OutputDirectory
    {
        get => OutputDirectoryTextBox.Text.Trim();
        set => OutputDirectoryTextBox.Text = value;
    }

    public bool RecordingMetricsLogEnabled
    {
        get => RecordingMetricsLogCheckBox.IsChecked == true;
        set => RecordingMetricsLogCheckBox.IsChecked = value;
    }

    public bool TranscriptionEnabled
    {
        get => TranscriptionEnabledCheckBox.IsChecked == true;
        set => TranscriptionEnabledCheckBox.IsChecked = value;
    }

    public bool AutoTranscriptionAfterRecord
    {
        get => AutoTranscriptionCheckBox.IsChecked == true;
        set => AutoTranscriptionCheckBox.IsChecked = value;
    }

    public bool TranscriptionToastNotificationEnabled
    {
        get => ToastNotificationCheckBox.IsChecked == true;
        set => ToastNotificationCheckBox.IsChecked = value;
    }

    public TranscriptionExecutionMode TranscriptionExecutionMode
    {
        get => GetSelectedTag(ExecutionModeComboBox, VoxArchive.Domain.TranscriptionExecutionMode.Auto);
        set => SelectByTag(ExecutionModeComboBox, value);
    }

    public TranscriptionModel TranscriptionModel
    {
        get => GetSelectedTag(ModelComboBox, VoxArchive.Domain.TranscriptionModel.Small);
        set => SelectByTag(ModelComboBox, value);
    }

    public string TranscriptionLanguage
    {
        get => GetSelectedStringTag(LanguageComboBox, "ja");
        set => SelectByStringTag(LanguageComboBox, value, "ja");
    }

    public TranscriptionPriority AutoTranscriptionPriority
    {
        get => GetSelectedTag(AutoPriorityComboBox, VoxArchive.Domain.TranscriptionPriority.Low);
        set => SelectByTag(AutoPriorityComboBox, value);
    }

    public TranscriptionPriority ManualTranscriptionPriority
    {
        get => GetSelectedTag(ManualPriorityComboBox, VoxArchive.Domain.TranscriptionPriority.Normal);
        set => SelectByTag(ManualPriorityComboBox, value);
    }

    public TranscriptionOutputFormats TranscriptionOutputFormats
    {
        get => BuildOutputFormats();
        set => ApplyOutputFormats(value);
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnTitleBarCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnBrowseOutputDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "録音ファイルの保存先を選択",
            InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : null
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    private void OnToggleHotkeyCaptureClick(object sender, RoutedEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            _isCapturingHotkey = true;
            _capturedHotkeyText = StartStopHotkeyText;
            HotkeyCaptureButton.Content = "確定";
            StartStopHotkeyTextBox.Text = "キー入力待ち...";
            Keyboard.Focus(this);
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(_capturedHotkeyText, out _, out var normalizedHotkey))
        {
            ModernDialog.Show(this, "その組み合わせはショートカットとして利用できません。別のキーを指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isCapturingHotkey = false;
        HotkeyCaptureButton.Content = "キー設定";
        StartStopHotkeyText = normalizedHotkey;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isCapturingHotkey = false;
            HotkeyCaptureButton.Content = "キー設定";
            StartStopHotkeyTextBox.Text = _capturedHotkeyText;
            e.Handled = true;
            return;
        }

        if (KeyboardShortcutHelper.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (KeyboardShortcutHelper.TryBuildFromInput(Keyboard.Modifiers, key, out var normalizedHotkey))
        {
            _capturedHotkeyText = normalizedHotkey;
            StartStopHotkeyTextBox.Text = normalizedHotkey;
        }
        else
        {
            StartStopHotkeyTextBox.Text = "未対応の組み合わせです";
        }

        e.Handled = true;
    }

    private void OnCheckEnvironmentClick(object sender, RoutedEventArgs e)
    {
        _ = RefreshEnvironmentStatusAsync();
    }

    private void OnTranscriptionEnvironmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEnvironmentAutoCheck)
        {
            return;
        }

        // 自動実行は行わず、明示的な「環境チェック」押下時のみ判定する。
        SetDefaultEnvironmentStatus();
    }

    private async Task RefreshEnvironmentStatusAsync()
    {
        if (Interlocked.CompareExchange(ref _environmentCheckInProgress, 1, 0) != 0)
        {
            return;
        }

        var checkVersion = Interlocked.Increment(ref _environmentCheckVersion);
        SetEnvironmentCheckUiState(isChecking: true);

        try
        {
            var options = BuildTemporaryOptions();
            var status = await Task.Run(() => _whisperTranscriptionService.CheckEnvironment(options));
            if (checkVersion != _environmentCheckVersion)
            {
                return;
            }

            var lines = new List<string>
            {
                status.RuntimeMessage,
                status.ModelMessage,
                status.CudaMessage,
                status.DetailMessage
            };

            TranscriptionStatusTextBlock.Text = string.Join(Environment.NewLine, lines.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (!status.RuntimeAvailable || !status.ModelInstalled)
            {
                TranscriptionStatusTextBlock.Foreground = StatusErrorBrush;
                return;
            }

            if (TranscriptionExecutionMode == TranscriptionExecutionMode.CudaPreferred && !status.CudaAvailable)
            {
                TranscriptionStatusTextBlock.Foreground = StatusWarningBrush;
                return;
            }

            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
        }
        catch (Exception ex)
        {
            if (checkVersion != _environmentCheckVersion)
            {
                return;
            }

            TranscriptionStatusTextBlock.Foreground = StatusErrorBrush;
            TranscriptionStatusTextBlock.Text = $"環境チェック失敗: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _environmentCheckInProgress, 0);
            SetEnvironmentCheckUiState(isChecking: false);
        }
    }

    private void OnDownloadModelClick(object sender, RoutedEventArgs e)
    {
        _ = DownloadModelAsync();
    }

    private async Task DownloadModelAsync()
    {
        try
        {
            ToggleTranscriptionButtons(false);
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = "モデルをダウンロードしています...";
            var path = await _whisperModelStore.DownloadAsync(TranscriptionModel);
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = $"モデル取得完了: {path}";
        }
        catch (Exception ex)
        {
            TranscriptionStatusTextBlock.Foreground = StatusErrorBrush;
            TranscriptionStatusTextBlock.Text = $"モデル取得失敗: {ex.Message}";
        }
        finally
        {
            ToggleTranscriptionButtons(true);
        }
    }
    private void OnDeleteModelClick(object sender, RoutedEventArgs e)
    {
        _ = DeleteModelAsync();
    }
    private async Task DeleteModelAsync()
    {
        try
        {
            await _whisperModelStore.DeleteAsync(TranscriptionModel);
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = "モデルを削除しました。";
        }
        catch (Exception ex)
        {
            TranscriptionStatusTextBlock.Foreground = StatusErrorBrush;
            TranscriptionStatusTextBlock.Text = $"モデル削除失敗: {ex.Message}";
        }
    }
    private void SetDefaultEnvironmentStatus()
    {
        TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
        TranscriptionStatusTextBlock.Text = "環境チェックで文字起こし実行可否を確認できます。";
    }

    private void SetEnvironmentCheckUiState(bool isChecking)
    {
        if (CheckEnvironmentButton is null)
        {
            return;
        }

        CheckEnvironmentButton.IsEnabled = !isChecking;
        CheckEnvironmentButton.Content = isChecking ? "確認中..." : "環境チェック";
    }
    private void ToggleTranscriptionButtons(bool isEnabled)
    {
        ExecutionModeComboBox.IsEnabled = isEnabled;
        ModelComboBox.IsEnabled = isEnabled;
        AutoPriorityComboBox.IsEnabled = isEnabled;
        ManualPriorityComboBox.IsEnabled = isEnabled;
        LanguageComboBox.IsEnabled = isEnabled;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(OffsetTextBox.Text, out var offsetMs))
        {
            ModernDialog.Show(this, "マイク遅延補正は整数で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (offsetMs < -1000 || offsetMs > 1000)
        {
            ModernDialog.Show(this, "マイク遅延補正は -1000 ～ 1000 の範囲で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseGain(DefaultSpeakerGainTextBox.Text, out var speakerGain))
        {
            ModernDialog.Show(this, "既定 Speaker 再生ゲインは数値で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseGain(DefaultMicGainTextBox.Text, out var micGain))
        {
            ModernDialog.Show(this, "既定 Mic 再生ゲインは数値で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (speakerGain < -60d || speakerGain > 48d || micGain < -60d || micGain > 48d)
        {
            ModernDialog.Show(this, "再生ゲインは -60dB ～ 48dB の範囲で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DefaultSpeakerPlaybackGainDb = speakerGain;
        DefaultMicPlaybackGainDb = micGain;

        if (_isCapturingHotkey)
        {
            ModernDialog.Show(this, "ショートカット設定中です。キー設定ボタンをもう一度押して確定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(StartStopHotkeyText, out _, out var normalizedHotkey))
        {
            ModernDialog.Show(this, "ショートカットは F12 や Ctrl+F12 のように指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartStopHotkeyText = normalizedHotkey;

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            ModernDialog.Show(this, "保存先を指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var formats = BuildOutputFormats();
        if (formats == TranscriptionOutputFormats.None)
        {
            ModernDialog.Show(this, "文字起こし出力形式を1つ以上選択してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static bool TryParseGain(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double ParseDouble(string text)
    {
        return TryParseGain(text, out var value) ? value : 0d;
    }

    private static TEnum GetSelectedTag<TEnum>(ComboBox comboBox, TEnum defaultValue)
        where TEnum : struct
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is TEnum value)
        {
            return value;
        }

        return defaultValue;
    }

    private static void SelectByTag<TEnum>(ComboBox comboBox, TEnum value)
        where TEnum : struct
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is TEnum tag && EqualityComparer<TEnum>.Default.Equals(tag, value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static string GetSelectedStringTag(ComboBox comboBox, string defaultValue)
    {
        if (comboBox.SelectedItem is ComboBoxItem item)
        {
            var text = item.Tag?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return defaultValue;
    }

    private static void SelectByStringTag(ComboBox comboBox, string value, string fallbackValue)
    {
        var target = string.IsNullOrWhiteSpace(value) ? fallbackValue : value.Trim();
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), fallbackValue, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private TranscriptionOutputFormats BuildOutputFormats()
    {
        var formats = TranscriptionOutputFormats.None;
        if (OutputTxtCheckBox.IsChecked == true)
        {
            formats |= TranscriptionOutputFormats.Txt;
        }

        if (OutputSrtCheckBox.IsChecked == true)
        {
            formats |= TranscriptionOutputFormats.Srt;
        }

        if (OutputVttCheckBox.IsChecked == true)
        {
            formats |= TranscriptionOutputFormats.Vtt;
        }

        if (OutputJsonCheckBox.IsChecked == true)
        {
            formats |= TranscriptionOutputFormats.Json;
        }

        return formats;
    }

    private void ApplyOutputFormats(TranscriptionOutputFormats formats)
    {
        OutputTxtCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Txt);
        OutputSrtCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Srt);
        OutputVttCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Vtt);
        OutputJsonCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Json);
    }

    private RecordingOptions BuildTemporaryOptions()
    {
        return new RecordingOptions
        {
            TranscriptionModel = TranscriptionModel,
            TranscriptionExecutionMode = TranscriptionExecutionMode
        };
    }
}

