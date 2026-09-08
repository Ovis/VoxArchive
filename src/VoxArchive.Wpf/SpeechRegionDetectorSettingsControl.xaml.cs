using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 共通発話検出で利用するSilero VADモデル管理と設定編集UIを提供する
/// </summary>
public partial class SpeechRegionDetectorSettingsControl : UserControl
{
    private readonly ISpeechRegionDetectorModelApplicationService _modelService;
    private CancellationTokenSource? _installCancellation;
    private bool _isBusy;
    private SileroVadSettings _settings = new();

    /// <summary>
    /// 発話検出モデル管理Facadeを利用してControlを初期化する
    /// </summary>
    public SpeechRegionDetectorSettingsControl(ISpeechRegionDetectorModelApplicationService modelService)
    {
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        InitializeComponent();
        ConfigureThresholdControl();
        ApplySettings(_settings);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 親設定画面が保持するSilero VAD編集値を取得・設定する
    /// </summary>
    public SileroVadSettings Settings
    {
        get => _settings with { Threshold = ThresholdNumericUpDown.Value };
        set
        {
            _settings = value ?? throw new ArgumentNullException(nameof(value));
            ApplySettings(_settings);
        }
    }

    private void ConfigureThresholdControl()
    {
        ThresholdNumericUpDown.Minimum = 0.01d;
        ThresholdNumericUpDown.Maximum = 0.99d;
        ThresholdNumericUpDown.Increment = 0.01d;
        ThresholdNumericUpDown.DecimalPlaces = 2;
        ThresholdNumericUpDown.Value = 0.50d;
    }

    private void ApplySettings(SileroVadSettings settings)
    {
        ThresholdNumericUpDown.Value = settings.Threshold;
        _settings = settings;
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
            var status = await Task.Run(_modelService.Inspect);
            ApplyStatus(status);
            if (preserveMessage)
            {
                MessageTextBlock.Text = previousMessage;
            }
        }
        catch (Exception ex)
        {
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

        var status = await Task.Run(_modelService.Inspect);
        var force = !string.Equals(status.State, "Missing", StringComparison.OrdinalIgnoreCase);
        _installCancellation = new CancellationTokenSource();
        SetBusyState(isBusy: true, allowCancel: true);
        ShowProgress(new SpeechRegionDetectorModelTransferInfo(0, null, null, IsValidating: false));
        MessageTextBlock.Text = string.Empty;

        try
        {
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
            _installCancellation.Dispose();
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
            "Silero VADモデルを削除します。設定値は維持され、モデルがない間は音量ベースVADへ自動的に切り替わります。",
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

        // 詳細ダイアログではThresholdを編集しないため、親Controlの現在値を維持して4項目だけ反映する。
        _settings = dialog.ResultSettings with { Threshold = current.Threshold };
    }

    private void ApplyStatus(SpeechRegionDetectorModelStatusInfo status)
    {
        ModelStatusTextBlock.Text = status.State.ToLowerInvariant() switch
        {
            "missing" => "未取得",
            "available" => "利用可能",
            "unavailable" => "利用不可",
            "checking" => "確認中",
            _ => "未確認"
        };

        DeleteButton.IsEnabled = !string.Equals(status.State, "Missing", StringComparison.OrdinalIgnoreCase);
        InstallButton.Content = string.Equals(status.State, "Missing", StringComparison.OrdinalIgnoreCase)
            ? "モデル取得"
            : "モデル再取得";
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
        ReverifyButton.IsEnabled = !isBusy;
        DeleteButton.IsEnabled = !isBusy;
        InstallButton.IsEnabled = !isBusy || allowCancel;
        if (allowCancel)
        {
            InstallButton.Content = "取得をキャンセル";
        }
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
