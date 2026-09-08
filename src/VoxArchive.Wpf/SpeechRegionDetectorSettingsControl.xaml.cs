using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 共通発話検出方式の選択とSilero VADモデル管理・設定編集UIを提供する
/// </summary>
public partial class SpeechRegionDetectorSettingsControl : UserControl
{
    private readonly ISpeechRegionDetectorModelApplicationService _modelService;
    private CancellationTokenSource? _installCancellation;
    private bool _isBusy;
    private SpeechRegionDetectorModelStatusInfo? _modelStatus;
    private SileroVadSettings _settings = new();

    /// <summary>
    /// 発話検出モデル管理Facadeを利用してControlを初期化する
    /// </summary>
    public SpeechRegionDetectorSettingsControl(ISpeechRegionDetectorModelApplicationService modelService)
    {
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        InitializeComponent();
        ApplySettings(_settings);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 親設定画面が保持する発話検出方式とSilero VAD編集値を取得・設定する
    /// </summary>
    public SileroVadSettings Settings
    {
        get => _settings with
        {
            Mode = VolumeBasedModeRadioButton.IsChecked == true
                ? SpeechRegionDetectorMode.VolumeBased
                : SpeechRegionDetectorMode.Silero
        };
        set
        {
            _settings = value ?? throw new ArgumentNullException(nameof(value));
            ApplySettings(_settings);
        }
    }

    private void ApplySettings(SileroVadSettings settings)
    {
        _settings = settings;

        if (settings.Mode == SpeechRegionDetectorMode.VolumeBased)
        {
            VolumeBasedModeRadioButton.IsChecked = true;
        }
        else
        {
            // 旧設定や未知のenum値を読み込んだ場合も、従来動作を維持するためSileroを選択状態にする。
            SileroModeRadioButton.IsChecked = true;
        }

        UpdateModeState();
    }

    private void OnVadModeChanged(object sender, RoutedEventArgs e)
    {
        if (SileroSettingsPanel is null)
        {
            return;
        }

        UpdateModeState();
    }

    private void UpdateModeState()
    {
        // 音量ベース選択中もSilero設定値は編集バッファに保持する。
        // 再びSileroへ戻した際にモデルや調整値をそのまま再利用できるよう、UIは隠さず無効化だけ行う。
        SileroSettingsPanel.IsEnabled = VolumeBasedModeRadioButton.IsChecked != true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = RefreshStateAsync();
    }

    private async Task RefreshStateAsync(bool preserveMessage = false)
    {
        var previousMessage = MessageTextBlock.Text;
        SetBusyState(isBusy: true, allowCancel: false);
        ModelStatusTextBlock.Text = "確認中";
        try
        {
            // 初回GetStateはnative loadを伴う場合があるためUI threadで同期実行しない。
            // native load側はglobal model-operation blockを取得し、文字起こしとの競合を避ける。
            var status = await Task.Run(_modelService.Inspect);
            ApplyStatus(status);
            if (preserveMessage)
            {
                MessageTextBlock.Text = previousMessage;
            }
        }
        catch (Exception ex)
        {
            _modelStatus = null;
            ModelStatusTextBlock.Text = "確認失敗";
            MessageTextBlock.Text = BuildOperationErrorMessage("モデル状態確認", ex);
        }
        finally
        {
            SetBusyState(isBusy: false, allowCancel: false);
        }
    }

    private async void OnReverifyClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusyState(isBusy: true, allowCancel: false);
        ModelStatusTextBlock.Text = "確認中";
        MessageTextBlock.Text = "Silero VADモデルを読み込んで確認しています。";
        try
        {
            var status = await _modelService.ReverifyAsync();
            ApplyStatus(status);
            MessageTextBlock.Text = status.IsReady
                ? "Silero VADモデルを利用できます。"
                : "モデルを読み込めませんでした。再取得するか、音量ベースVADを利用してください。";
        }
        catch (Exception ex)
        {
            MessageTextBlock.Text = BuildOperationErrorMessage("モデル再確認", ex);
        }
        finally
        {
            SetBusyState(isBusy: false, allowCancel: false);
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_installCancellation is not null)
        {
            _installCancellation.Cancel();
            InstallButton.IsEnabled = false;
            InstallButton.Content = "キャンセル中...";
            return;
        }

        if (_isBusy)
        {
            return;
        }

        try
        {
            // 取得済みモデルではtransactionを再取得モードで動かす必要があるため、開始直前の状態を確認する。
            // 状態確認自体がnative loadを伴う可能性があるので、取得処理と同じ例外分類へ含めてUIへ伝える。
            var status = await Task.Run(_modelService.Inspect);
            var force = !string.Equals(status.State, "Missing", StringComparison.OrdinalIgnoreCase);
            _installCancellation = new CancellationTokenSource();
            SetBusyState(isBusy: true, allowCancel: true);
            ShowProgress(new SpeechRegionDetectorModelTransferInfo(0, null, null, IsValidating: false));
            MessageTextBlock.Text = string.Empty;

            var progress = new Progress<SpeechRegionDetectorModelTransferInfo>(ShowProgress);
            await _modelService.InstallAsync(force, progress, _installCancellation.Token);
            MessageTextBlock.Text = "Silero VADモデルの取得が完了しました。";
        }
        catch (OperationCanceledException)
        {
            MessageTextBlock.Text = "Silero VADモデルの取得をキャンセルしました。";
        }
        catch (Exception ex)
        {
            MessageTextBlock.Text = BuildOperationErrorMessage("モデル取得", ex);
        }
        finally
        {
            _installCancellation?.Dispose();
            _installCancellation = null;
            HideProgress();
            SetBusyState(isBusy: false, allowCancel: false);
            await RefreshStateAsync(preserveMessage: true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var result = ModernDialog.Show(
            owner,
            "Silero VADモデルを削除します。設定値は維持され、Silero VADを選択した状態でモデルがない場合は音量ベースVADへ自動的に切り替わります。",
            "Silero VADモデル削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        SetBusyState(isBusy: true, allowCancel: false);
        try
        {
            await _modelService.DeleteAsync();
            MessageTextBlock.Text = "Silero VADモデルを削除しました。";
        }
        catch (Exception ex)
        {
            MessageTextBlock.Text = BuildOperationErrorMessage("モデル削除", ex);
        }
        finally
        {
            SetBusyState(isBusy: false, allowCancel: false);
            await RefreshStateAsync(preserveMessage: true);
        }
    }

    private void OnAdvancedSettingsClick(object sender, RoutedEventArgs e)
    {
        var current = Settings;
        var dialog = new SpeechRegionDetectorAdvancedSettingsWindow(current)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 発話検出方式は共通画面で選択する責務のため、詳細設定の結果へ現在の方式だけを戻して保持する。
        _settings = dialog.ResultSettings with
        {
            Mode = current.Mode
        };
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        // 「既定値に戻す」はSileroの5項目だけを対象とし、利用者が選択したVAD方式は変更しない。
        // 音量ベースを選んだまま事前にSilero設定だけ標準値へ戻せるよう、Modeを明示的に維持する。
        ApplySettings(new SileroVadSettings { Mode = Settings.Mode });
    }

    private void ApplyStatus(SpeechRegionDetectorModelStatusInfo status)
    {
        _modelStatus = status;
        ModelStatusTextBlock.Text = status.State.ToLowerInvariant() switch
        {
            "missing" => "未取得",
            "available" => "利用可能",
            "unavailable" => "利用不可",
            "checking" => "確認中",
            _ => "未確認"
        };

        InstallButton.Content = string.Equals(status.State, "Missing", StringComparison.OrdinalIgnoreCase)
            ? "モデル取得"
            : "モデル再取得";
        UpdateActionState(allowCancel: false);
    }

    private void ShowProgress(SpeechRegionDetectorModelTransferInfo progress)
    {
        DownloadProgressBar.Visibility = Visibility.Visible;
        ProgressTextBlock.Visibility = Visibility.Visible;

        if (progress.IsValidating)
        {
            DownloadProgressBar.IsIndeterminate = true;
            ProgressTextBlock.Text = "モデルを検証しています…";
            return;
        }

        DownloadProgressBar.IsIndeterminate = progress.Percent is null;
        if (progress.Percent is { } percent)
        {
            DownloadProgressBar.Value = percent;
        }

        var fileName = string.IsNullOrWhiteSpace(progress.CurrentFileName)
            ? "モデル"
            : progress.CurrentFileName;
        ProgressTextBlock.Text = progress.TotalBytes is { } total
            ? $"{fileName}: {progress.Percent ?? 0d:F0}%（{FormatBytes(progress.BytesReceived)} / {FormatBytes(total)}）"
            : $"{fileName}: {FormatBytes(progress.BytesReceived)}";
    }

    private void HideProgress()
    {
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
        ProgressTextBlock.Visibility = Visibility.Collapsed;
        ProgressTextBlock.Text = string.Empty;
    }

    private void SetBusyState(bool isBusy, bool allowCancel)
    {
        _isBusy = isBusy;
        UpdateActionState(allowCancel);
        if (allowCancel)
        {
            InstallButton.Content = "取得をキャンセル";
        }
    }

    private void UpdateActionState(bool allowCancel)
    {
        ReverifyButton.IsEnabled = !_isBusy;
        InstallButton.IsEnabled = !_isBusy || allowCancel;

        // busy状態だけで削除可否を決めると、Missing判定後のfinallyで削除ボタンが再度有効になる。
        // モデル状態と操作中状態を一箇所で合成し、状態更新順序に依存しないようにする。
        var modelExists = _modelStatus is not null
            && !string.Equals(_modelStatus.State, "Missing", StringComparison.OrdinalIgnoreCase);
        DeleteButton.IsEnabled = !_isBusy && modelExists;
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    private static string BuildOperationErrorMessage(string operation, Exception exception)
        => exception switch
        {
            HttpRequestException => $"{operation}に失敗しました。ネットワーク接続を確認してください。",
            UnauthorizedAccessException => $"{operation}に失敗しました。モデル保存先へアクセスできません。",
            IOException => $"{operation}に失敗しました。空き容量またはモデル保存先を確認してください。",
            InvalidDataException => $"{operation}に失敗しました。取得したモデルを利用可能な状態で読み込めませんでした。",
            InvalidOperationException => $"{operation}を開始できません。文字起こしまたは別のモデル操作が完了してから再実行してください。",
            _ => $"{operation}に失敗しました。診断ログを確認してください。"
        };
}
