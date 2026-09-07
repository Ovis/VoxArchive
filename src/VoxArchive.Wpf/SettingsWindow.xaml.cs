using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 録音・文字起こしに関するアプリケーション設定を編集するWindowを提供する
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ITranscriptionApplicationService _transcriptionService;

    private static readonly Brush StatusDefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9BB4D1"));
    private static readonly Brush StatusErrorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9A9A"));

    private bool _isCapturingHotkey;
    private bool _suppressEnvironmentAutoCheck = true;
    private string _capturedHotkeyText = string.Empty;
    private int _environmentCheckVersion;
    private int _environmentCheckInProgress;

    /// <summary>
    /// アプリケーションのDIコンテナから文字起こしFacadeを解決して設定Windowを初期化する
    /// </summary>
    public SettingsWindow()
    {
        var app = System.Windows.Application.Current as App
            ?? throw new InvalidOperationException("VoxArchiveアプリケーションを取得できません。");
        _transcriptionService = app.Services.GetRequiredService<ITranscriptionApplicationService>();
        InitializeWindow();
    }

    /// <summary>
    /// 設定Windowが利用する文字起こしFacadeを明示して初期化する
    /// </summary>
    public SettingsWindow(ITranscriptionApplicationService transcriptionService)
    {
        _transcriptionService = transcriptionService;
        InitializeWindow();
    }

    private void InitializeWindow()
    {
        _suppressEnvironmentAutoCheck = true;
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;

        InitializeTranscriptionTabs();
        WhisperModelId = "small";
        ReazonSpeechModelId = "ja";
        AutoTranscriptionPriority = TranscriptionPriority.Low;
        ManualTranscriptionPriority = TranscriptionPriority.Normal;
        TranscriptionLanguage = string.Empty;
        OutputTxtCheckBox.IsChecked = true;
        SetDefaultEnvironmentStatus();

        _suppressEnvironmentAutoCheck = false;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _transcriptionService.ModelStateChanged -= OnModelManagerStateChanged;
        base.OnClosed(e);
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

    public string FfmpegExecutablePath
    {
        get => FfmpegPathTextBox.Text.Trim();
        set => FfmpegPathTextBox.Text = value;
    }

    public bool RecordingMetricsLogEnabled
    {
        get => RecordingMetricsLogCheckBox.IsChecked == true;
        set => RecordingMetricsLogCheckBox.IsChecked = value;
    }

    public bool TranscriptionDiagnosticsLogEnabled
    {
        get => TranscriptionDiagnosticsLogCheckBox.IsChecked == true;
        set => TranscriptionDiagnosticsLogCheckBox.IsChecked = value;
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

    /// <summary>
    /// Whisperが公開する実行方式descriptorを設定画面へ投影する
    /// </summary>
    public IReadOnlyList<TranscriptionExecutionModeInfo> WhisperExecutionModes
    {
        set => PopulateExecutionModes(value);
    }

    /// <summary>
    /// Whisperへ要求する実行方式をEngineが公開する安定文字列IDで取得・設定する
    /// </summary>
    public string WhisperExecutionMode
    {
        get => (ExecutionModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim() ?? string.Empty;
        set => SelectExecutionMode(value);
    }

    /// <summary>
    /// Whisperで使用する論理モデルIDを取得・設定する
    /// </summary>
    public string WhisperModelId
    {
        get => string.IsNullOrWhiteSpace(WhisperModelManagerControl.SelectedModelId)
            ? "small"
            : WhisperModelManagerControl.SelectedModelId!;
        set => WhisperModelManagerControl.SelectedModelId = string.IsNullOrWhiteSpace(value)
            ? "small"
            : value.Trim().ToLowerInvariant();
    }

    public string TranscriptionLanguage
    {
        get
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString()?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }
        set => SelectLanguage(value);
    }

    public TranscriptionPriority AutoTranscriptionPriority
    {
        get => GetSelectedTag(AutoPriorityComboBox, TranscriptionPriority.Low);
        set => SelectByTag(AutoPriorityComboBox, value);
    }

    public TranscriptionPriority ManualTranscriptionPriority
    {
        get => GetSelectedTag(ManualPriorityComboBox, TranscriptionPriority.Normal);
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

    private void OnBrowseFfmpegPathClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "ffmpeg 実行ファイルを選択",
            Filter = "ffmpeg.exe|ffmpeg.exe|実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(FfmpegExecutablePath) && File.Exists(FfmpegExecutablePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(FfmpegExecutablePath);
            dialog.FileName = Path.GetFileName(FfmpegExecutablePath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegExecutablePath = dialog.FileName;
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
        if (!_suppressEnvironmentAutoCheck)
        {
            SetDefaultEnvironmentStatus();
        }
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
            var diagnostics = await _transcriptionService.DiagnoseEngineAsync("whisper");
            if (checkVersion != _environmentCheckVersion)
            {
                return;
            }

            var errors = diagnostics
                .Where(x => x.Level == TranscriptionDiagnosticLevel.Error)
                .Select(x => x.Message)
                .ToArray();
            var warnings = diagnostics
                .Where(x => x.Level == TranscriptionDiagnosticLevel.Warning)
                .Select(x => x.Message)
                .ToArray();
            var information = diagnostics
                .Where(x => x.Level == TranscriptionDiagnosticLevel.Information)
                .Select(x => x.Message)
                .ToArray();

            WhisperEnvironmentStatusTextBlock.Foreground = errors.Length > 0
                ? StatusErrorBrush
                : StatusDefaultBrush;
            var messages = errors.Length > 0
                ? errors
                : warnings.Length > 0
                    ? warnings.Concat(information).ToArray()
                    : information;
            WhisperEnvironmentStatusTextBlock.Text = messages.Length == 0
                ? "Whisperの診断項目はありません。"
                : string.Join(Environment.NewLine, messages);
        }
        catch (Exception ex)
        {
            if (checkVersion != _environmentCheckVersion)
            {
                return;
            }

            WhisperEnvironmentStatusTextBlock.Foreground = StatusErrorBrush;
            WhisperEnvironmentStatusTextBlock.Text = $"環境チェックに失敗しました。{Environment.NewLine}{ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _environmentCheckInProgress, 0);
            SetEnvironmentCheckUiState(isChecking: false);
        }
    }

    private void SetDefaultEnvironmentStatus()
    {
        WhisperEnvironmentStatusTextBlock.Foreground = StatusDefaultBrush;
        WhisperEnvironmentStatusTextBlock.Text = "環境チェックで利用可能なWhisper実行方式を確認できます。";
    }

    private void SetEnvironmentCheckUiState(bool isChecking)
    {
        CheckEnvironmentButton.IsEnabled = !isChecking;
        CheckEnvironmentButton.Content = isChecking ? "確認中..." : "環境チェック";
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

    private void PopulateExecutionModes(IReadOnlyList<TranscriptionExecutionModeInfo> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);

        ExecutionModeComboBox.Items.Clear();
        foreach (var mode in modes)
        {
            ExecutionModeComboBox.Items.Add(new ComboBoxItem
            {
                Content = mode.DisplayName,
                Tag = mode.Id
            });
        }

        ExecutionModeComboBox.IsEnabled = ExecutionModeComboBox.Items.Count > 0;
        if (ExecutionModeComboBox.Items.Count > 0)
        {
            ExecutionModeComboBox.SelectedIndex = 0;
        }
    }

    private void SelectExecutionMode(string? value)
    {
        var target = value?.Trim() ?? string.Empty;
        foreach (var item in ExecutionModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString()?.Trim(), target, StringComparison.OrdinalIgnoreCase))
            {
                ExecutionModeComboBox.SelectedItem = item;
                return;
            }
        }

        if (ExecutionModeComboBox.Items.Count > 0)
        {
            ExecutionModeComboBox.SelectedIndex = 0;
        }
    }

    private void SelectLanguage(string? value)
    {
        var target = value?.Trim() ?? string.Empty;
        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString()?.Trim() ?? string.Empty;
            if (string.Equals(tag, target, StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedItem = item;
                return;
            }
        }

        LanguageComboBox.SelectedIndex = 0;
    }

    private TranscriptionOutputFormats BuildOutputFormats()
    {
        // canonical JSONはCommonが常に保存するため、UIでは派生形式だけを選択する。
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

        return formats;
    }

    private void ApplyOutputFormats(TranscriptionOutputFormats formats)
    {
        OutputTxtCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Txt);
        OutputSrtCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Srt);
        OutputVttCheckBox.IsChecked = formats.HasFlag(TranscriptionOutputFormats.Vtt);
    }
}
