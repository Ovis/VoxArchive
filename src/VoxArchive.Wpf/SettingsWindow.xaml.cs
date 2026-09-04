using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 録音・文字起こしに関するアプリケーション設定を編集するWindowを提供する
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly WhisperModelStore _whisperModelStore;
    private readonly WhisperTranscriptionService _whisperTranscriptionService;
    private readonly TranscriptionModelManager _modelManager;

    private static readonly Brush StatusDefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9BB4D1"));
    private static readonly Brush StatusErrorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9A9A"));

    private bool _isCapturingHotkey;
    private bool _suppressEnvironmentAutoCheck = true;
    private string _capturedHotkeyText = string.Empty;
    private int _environmentCheckVersion;
    private int _environmentCheckInProgress;

    /// <summary>
    /// アプリケーションのDIコンテナから文字起こし関連サービスを解決して設定Windowを初期化する
    /// </summary>
    public SettingsWindow()
    {
        var app = System.Windows.Application.Current as App
            ?? throw new InvalidOperationException("VoxArchiveアプリケーションを取得できません。");
        _whisperModelStore = app.Services.GetRequiredService<WhisperModelStore>();
        _whisperTranscriptionService = app.Services.GetRequiredService<WhisperTranscriptionService>();
        _modelManager = app.Services.GetRequiredService<TranscriptionModelManager>();
        InitializeWindow();
    }

    /// <summary>
    /// 設定Windowが利用するアプリケーション共有サービスを明示して初期化する
    /// </summary>
    public SettingsWindow(
        WhisperModelStore whisperModelStore,
        WhisperTranscriptionService whisperTranscriptionService,
        TranscriptionModelManager modelManager)
    {
        _whisperModelStore = whisperModelStore;
        _whisperTranscriptionService = whisperTranscriptionService;
        _modelManager = modelManager;
        InitializeWindow();
    }

    private void InitializeWindow()
    {
        _suppressEnvironmentAutoCheck = true;
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;

        InitializeTranscriptionTabs();
        TranscriptionExecutionMode = TranscriptionExecutionMode.Auto;
        TranscriptionModel = TranscriptionModel.Small;
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
        _modelManager.StateChanged -= OnModelManagerStateChanged;
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

    public TranscriptionExecutionMode TranscriptionExecutionMode
    {
        get => GetSelectedTag(ExecutionModeComboBox, VoxArchive.Domain.TranscriptionExecutionMode.Auto);
        set
        {
            // CudaPreferredは旧設定との互換値としてだけ残っている。現在のUIではAutoへ正規化し、
            // CUDA 13→CUDA 12→Vulkan→CPUの自動選択へ統一する。
            var normalized = value == VoxArchive.Domain.TranscriptionExecutionMode.CpuOnly
                ? VoxArchive.Domain.TranscriptionExecutionMode.CpuOnly
                : VoxArchive.Domain.TranscriptionExecutionMode.Auto;
            SelectByTag(ExecutionModeComboBox, normalized);
        }
    }

    public TranscriptionModel TranscriptionModel
    {
        get => ParseWhisperModelId(WhisperModelManagerControl.SelectedModelId);
        set => WhisperModelManagerControl.SelectedModelId = ToWhisperModelId(value);
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
            var options = BuildTemporaryOptions();
            var status = await Task.Run(() => _whisperTranscriptionService.CheckEnvironment(options));
            if (checkVersion != _environmentCheckVersion)
            {
                return;
            }

            if (!status.RuntimeAvailable)
            {
                WhisperEnvironmentStatusTextBlock.Foreground = StatusErrorBrush;
                WhisperEnvironmentStatusTextBlock.Text = string.Join(
                    Environment.NewLine,
                    new[] { status.RuntimeMessage, status.DetailMessage }.Where(text => !string.IsNullOrWhiteSpace(text)));
                return;
            }

            WhisperEnvironmentStatusTextBlock.Foreground = StatusDefaultBrush;
            if (TranscriptionExecutionMode == VoxArchive.Domain.TranscriptionExecutionMode.CpuOnly)
            {
                WhisperEnvironmentStatusTextBlock.Text = "CPU を利用できます。";
                return;
            }

            if (status.CudaAvailable)
            {
                WhisperEnvironmentStatusTextBlock.Text = "CUDA を利用できます。自動モードでは利用可能な処理方式を優先順位に従って選択します。";
                return;
            }

            // CUDAが利用できない場合もVulkan/CPUへフォールバックできるため、成功状態のまま理由を補足する。
            var runtime = WhisperRuntimeProbe.Check();
            var availableFallback = runtime.Details.FirstOrDefault(detail =>
                (detail.StartsWith("Vulkan", StringComparison.Ordinal) || detail.StartsWith("CPU", StringComparison.Ordinal))
                && detail.EndsWith("利用可能", StringComparison.Ordinal));
            WhisperEnvironmentStatusTextBlock.Text = availableFallback is null
                ? "Whisperランタイムを利用できます。CUDAは利用できません。"
                : $"{availableFallback.Replace(": 利用可能", string.Empty, StringComparison.Ordinal)} を利用できます。CUDAは利用できません。";
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

        // 未知の言語コードはUIで誤表示せず「指定なし」へ戻す。設定保存時も空文字となりWhisper側の自動判定になる。
        LanguageComboBox.SelectedIndex = 0;
    }

    private TranscriptionOutputFormats BuildOutputFormats()
    {
        // canonical JSONは文字起こし結果の内部データなので、UIの選択状態にかかわらず必ず保存する。
        var formats = TranscriptionOutputFormats.Json;
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

    private RecordingOptions BuildTemporaryOptions()
    {
        return new RecordingOptions
        {
            TranscriptionModel = TranscriptionModel,
            TranscriptionExecutionMode = TranscriptionExecutionMode
        };
    }

    private static string ToWhisperModelId(TranscriptionModel model)
    {
        return model switch
        {
            VoxArchive.Domain.TranscriptionModel.Tiny => "tiny",
            VoxArchive.Domain.TranscriptionModel.Base => "base",
            VoxArchive.Domain.TranscriptionModel.Small => "small",
            VoxArchive.Domain.TranscriptionModel.Medium => "medium",
            VoxArchive.Domain.TranscriptionModel.LargeV3 => "large-v3",
            _ => "small"
        };
    }

    private static TranscriptionModel ParseWhisperModelId(string? modelId)
    {
        return modelId switch
        {
            "tiny" => VoxArchive.Domain.TranscriptionModel.Tiny,
            "base" => VoxArchive.Domain.TranscriptionModel.Base,
            "medium" => VoxArchive.Domain.TranscriptionModel.Medium,
            "large-v3" => VoxArchive.Domain.TranscriptionModel.LargeV3,
            _ => VoxArchive.Domain.TranscriptionModel.Small
        };
    }
}