using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Windowの文字起こしタブとアプリケーション共有モデル管理を接続する
/// </summary>
public partial class SettingsWindow
{
    private const string WhisperEngineId = "whisper";
    private const string ReazonSpeechEngineId = "reazonspeech";

    private bool _whisperTabVisited;
    private bool _reazonSpeechTabVisited;
    private SpeechRegionDetectorSettingsControl? _speechRegionDetectorSettingsControl;

    /// <summary>新規文字起こしで既定として使用するEngineの安定IDを取得・設定する</summary>
    public string DefaultTranscriptionEngine
    {
        get
        {
            if (DefaultEngineComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                return id;
            }

            return WhisperEngineId;
        }
        set
        {
            var normalized = string.Equals(value, ReazonSpeechEngineId, StringComparison.OrdinalIgnoreCase)
                ? ReazonSpeechEngineId
                : WhisperEngineId;
            SelectComboBoxStringTag(DefaultEngineComboBox, normalized);
        }
    }

    /// <summary>ReazonSpeechで使用する論理モデルIDを取得・設定する</summary>
    public string ReazonSpeechModelId
    {
        get => string.IsNullOrWhiteSpace(ReazonSpeechModelManagerControl.SelectedModelId)
            ? "ja"
            : ReazonSpeechModelManagerControl.SelectedModelId!;
        set => ReazonSpeechModelManagerControl.SelectedModelId = string.IsNullOrWhiteSpace(value) ? "ja" : value.Trim().ToLowerInvariant();
    }

    private void InitializeTranscriptionTabs()
    {
        PopulateModelChoices(WhisperEngineId, WhisperModelManagerControl);
        PopulateModelChoices(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);
        InitializeSpeechRegionDetectorSettingsControl();

        WhisperModelManagerControl.SelectedModelChanged += OnWhisperModelSelectionChanged;
        WhisperModelManagerControl.VerifyRequested += OnWhisperModelVerifyRequested;
        WhisperModelManagerControl.InstallRequested += OnWhisperModelInstallRequested;
        WhisperModelManagerControl.DeleteRequested += OnWhisperModelDeleteRequested;

        ReazonSpeechModelManagerControl.SelectedModelChanged += OnReazonSpeechModelSelectionChanged;
        ReazonSpeechModelManagerControl.VerifyRequested += OnReazonSpeechModelVerifyRequested;
        ReazonSpeechModelManagerControl.InstallRequested += OnReazonSpeechModelInstallRequested;
        ReazonSpeechModelManagerControl.DeleteRequested += OnReazonSpeechModelDeleteRequested;

        _transcriptionService.ModelStateChanged += OnModelManagerStateChanged;
        TranscriptionTabControl.SelectedIndex = 0;
    }

    private void InitializeSpeechRegionDetectorSettingsControl()
    {
        // テスト用constructorではApplication DIが存在しない場合があるため、Facadeを解決できる実アプリだけControlを追加する。
        // PresentationからSilero具象型へは依存せず、専用Application Facadeだけを利用する。
        var app = System.Windows.Application.Current as App;
        var modelService = app?.Services.GetService<ISpeechRegionDetectorModelApplicationService>();
        if (modelService is null
            || TranscriptionTabControl.Items.Count == 0
            || TranscriptionTabControl.Items[0] is not TabItem commonTab
            || commonTab.Content is not Grid commonGrid)
        {
            return;
        }

        var leftColumn = commonGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(x => Grid.GetColumn(x) == 0);
        if (leftColumn is null)
        {
            return;
        }

        _speechRegionDetectorSettingsControl = new SpeechRegionDetectorSettingsControl(modelService);
        leftColumn.Children.Add(_speechRegionDetectorSettingsControl);
    }

    private void PopulateModelChoices(string engineId, TranscriptionModelManagerControl control)
    {
        control.Models.Clear();
        foreach (var model in _transcriptionService.GetAvailableModels(engineId))
        {
            control.Models.Add(new TranscriptionModelChoice(model.Id, model.DisplayName));
        }

        if (control.Models.Count > 0 && string.IsNullOrWhiteSpace(control.SelectedModelId))
        {
            control.SelectedModelId = control.Models[0].Id;
        }
    }

    private void OnTranscriptionTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || e.Source != TranscriptionTabControl)
        {
            return;
        }

        if (TranscriptionTabControl.SelectedIndex == 1)
        {
            _whisperTabVisited = true;
            RefreshModelControl(WhisperEngineId, WhisperModelManagerControl);
        }
        else if (TranscriptionTabControl.SelectedIndex == 2)
        {
            _reazonSpeechTabVisited = true;
            RefreshModelControl(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);
        }
    }

    private void OnWhisperModelSelectionChanged(object? sender, EventArgs e)
    {
        if (_whisperTabVisited)
        {
            RefreshModelControl(WhisperEngineId, WhisperModelManagerControl);
        }

        SetDefaultEnvironmentStatus();
    }

    private void OnReazonSpeechModelSelectionChanged(object? sender, EventArgs e)
    {
        if (_reazonSpeechTabVisited)
        {
            RefreshModelControl(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);
        }
    }

    private void OnWhisperModelVerifyRequested(object? sender, EventArgs e)
        => _ = VerifyModelAsync(WhisperEngineId, WhisperModelManagerControl);

    private void OnReazonSpeechModelVerifyRequested(object? sender, EventArgs e)
        => _ = VerifyModelAsync(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);

    private void OnWhisperModelInstallRequested(object? sender, EventArgs e)
        => _ = InstallOrCancelModelAsync(WhisperEngineId, WhisperModelManagerControl);

    private void OnReazonSpeechModelInstallRequested(object? sender, EventArgs e)
        => _ = InstallOrCancelModelAsync(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);

    private void OnWhisperModelDeleteRequested(object? sender, EventArgs e)
        => _ = DeleteModelAsync(WhisperEngineId, WhisperModelManagerControl);

    private void OnReazonSpeechModelDeleteRequested(object? sender, EventArgs e)
        => _ = DeleteModelAsync(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);

    private async Task VerifyModelAsync(string engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        control.CanVerify = false;
        control.MessageText = "モデルファイルの完全性を確認しています...";
        try
        {
            var inspection = await _transcriptionService.ReverifyModelAsync(engineId, modelId);
            ApplyInspectionState(control, inspection.State);
            if (inspection.IsReady)
            {
                control.MessageText = "モデルファイルの完全性を確認しました。";
            }
            else
            {
                control.MessageText = "完全性確認で問題が見つかりました。モデルを再取得してください。";
            }
        }
        catch (Exception ex)
        {
            control.MessageText = BuildModelOperationErrorMessage("完全性確認", ex);
        }
        finally
        {
            RefreshModelControl(engineId, control, preserveMessage: true);
        }
    }

    private async Task InstallOrCancelModelAsync(string engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        var active = _transcriptionService.GetActiveModelDownload();
        if (active is not null
            && string.Equals(active.EngineId, engineId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(active.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            if (active.WaiterCount > 0)
            {
                var result = ModernDialog.Show(
                    this,
                    "このモデルの取得完了を待っている文字起こしがあります。\nモデル取得を中止すると、待機中の文字起こしも開始できなくなります。",
                    "モデル取得の中止",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
                if (result != MessageBoxResult.OK)
                {
                    return;
                }
            }

            _transcriptionService.CancelModelDownload(engineId, modelId);
            return;
        }

        try
        {
            var inspection = _transcriptionService.InspectModel(engineId, modelId);
            var force = !string.Equals(inspection.State, "Missing", StringComparison.OrdinalIgnoreCase);
            var progress = new Progress<TranscriptionModelTransferInfo>(_ => RefreshModelControl(engineId, control));
            var downloadTask = _transcriptionService.InstallModelAsync(engineId, modelId, force, progress);
            _ = ObserveSettingsDownloadAsync(downloadTask, engineId, modelId, control);
            RefreshModelControl(engineId, control);
        }
        catch (Exception ex)
        {
            control.MessageText = BuildModelOperationErrorMessage("モデル取得", ex);
        }
    }

    private async Task ObserveSettingsDownloadAsync(
        Task downloadTask,
        string engineId,
        string modelId,
        TranscriptionModelManagerControl control)
    {
        try
        {
            await downloadTask;
            if (IsLoaded)
            {
                control.MessageText = "モデル取得が完了しました。";
                RefreshModelControl(engineId, control, preserveMessage: true);
            }
            else
            {
                AppNotificationHub.Notify("VoxArchive", $"モデル取得完了: {GetModelDisplayName(engineId, modelId)}", System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
            {
                control.MessageText = "モデル取得をキャンセルしました。";
                RefreshModelControl(engineId, control, preserveMessage: true);
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                control.MessageText = BuildModelOperationErrorMessage("モデル取得", ex);
                RefreshModelControl(engineId, control, preserveMessage: true);
            }
            else
            {
                AppNotificationHub.Notify("VoxArchive", $"モデル取得に失敗しました: {GetModelDisplayName(engineId, modelId)}", System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
    }

    private async Task DeleteModelAsync(string engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        var displayName = GetModelDisplayName(engineId, modelId);
        var engineName = string.Equals(engineId, WhisperEngineId, StringComparison.OrdinalIgnoreCase) ? "Whisper" : "ReazonSpeech";
        var result = ModernDialog.Show(
            this,
            $"{engineName} / {displayName} のローカルモデルファイルを削除します。\nモデルの選択設定は維持され、再度利用するにはモデル取得が必要です。",
            "モデル削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _transcriptionService.DeleteModelAsync(engineId, modelId);
            control.MessageText = "モデルを削除しました。";
            RefreshModelControl(engineId, control, preserveMessage: true);
        }
        catch (Exception ex)
        {
            control.MessageText = BuildModelOperationErrorMessage("モデル削除", ex);
        }
    }

    private void RefreshModelControl(
        string engineId,
        TranscriptionModelManagerControl control,
        bool preserveMessage = false)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        var previousMessage = control.MessageText;
        control.ProgressVisibility = Visibility.Collapsed;
        control.ProgressPercent = 0;
        control.ProgressText = string.Empty;

        try
        {
            var inspection = _transcriptionService.InspectModel(engineId, modelId);
            ApplyInspectionState(control, inspection.State);

            var isProtected = _transcriptionService.IsModelProtected(engineId, modelId);
            var active = _transcriptionService.GetActiveModelDownload();
            var isCurrentDownload = active is not null
                && string.Equals(active.EngineId, engineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(active.ModelId, modelId, StringComparison.OrdinalIgnoreCase);

            if (isProtected)
            {
                control.StatusText = inspection.IsReady ? "使用中（取得済み）" : "使用中";
                control.CanVerify = false;
                control.CanInstall = false;
                control.CanDelete = false;
                control.MessageText = "文字起こしジョブがこのモデルを参照しています。ジョブ完了後に管理できます。";
                return;
            }

            if (isCurrentDownload && active is not null)
            {
                control.StatusText = active.IsCancelling ? "取得中止処理中" : "取得中";
                control.InstallButtonText = active.IsCancelling ? "取得をキャンセル中" : "取得をキャンセル";
                control.CanInstall = !active.IsCancelling;
                control.CanVerify = false;
                control.CanDelete = false;
                control.ProgressVisibility = Visibility.Visible;
                control.ProgressPercent = active.Percent;
                control.ProgressText = FormatProgress(active.BytesReceived, active.TotalBytes);
                if (!preserveMessage)
                {
                    control.MessageText = string.Empty;
                }
                return;
            }

            control.CanVerify = true;
            control.CanDelete = !string.Equals(inspection.State, "Missing", StringComparison.OrdinalIgnoreCase);
            control.InstallButtonText = string.Equals(inspection.State, "Missing", StringComparison.OrdinalIgnoreCase)
                ? "モデル取得"
                : "モデル再取得";
            control.CanInstall = active is null;

            if (active is not null && !preserveMessage)
            {
                control.MessageText = $"{active.EngineId} / {active.ModelDisplayName} のモデルを取得中です。";
            }
            else if (preserveMessage)
            {
                control.MessageText = previousMessage;
            }
            else
            {
                control.MessageText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            control.StatusText = "確認失敗";
            control.MessageText = BuildModelOperationErrorMessage("モデル状態確認", ex);
            control.CanVerify = false;
            control.CanInstall = false;
            control.CanDelete = false;
        }
    }

    private static void ApplyInspectionState(TranscriptionModelManagerControl control, string state)
    {
        control.StatusText = state.ToLowerInvariant() switch
        {
            "missing" => "未取得",
            "installed" => "取得済み",
            "incomplete" => "不完全",
            "corrupt" => "破損または不完全",
            _ => "未確認"
        };
    }

    private void OnModelManagerStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshVisitedModelControls);
            return;
        }

        RefreshVisitedModelControls();
    }

    private void RefreshVisitedModelControls()
    {
        if (_whisperTabVisited)
        {
            RefreshModelControl(WhisperEngineId, WhisperModelManagerControl);
        }

        if (_reazonSpeechTabVisited)
        {
            RefreshModelControl(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);
        }
    }

    private static bool TryGetSelectedModel(TranscriptionModelManagerControl control, out string modelId)
    {
        if (!string.IsNullOrWhiteSpace(control.SelectedModelId))
        {
            modelId = control.SelectedModelId.Trim();
            return true;
        }

        modelId = string.Empty;
        return false;
    }

    private string GetModelDisplayName(string engineId, string modelId)
    {
        return _transcriptionService.GetAvailableModels(engineId)
            .FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? modelId;
    }

    private static string FormatProgress(long received, long total)
    {
        if (total <= 0)
        {
            return FormatBytes(received);
        }

        var percent = Math.Clamp(received * 100d / total, 0d, 100d);
        return $"{percent:F0}%（{FormatBytes(received)} / {FormatBytes(total)}）";
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

    private static string BuildModelOperationErrorMessage(string operation, Exception exception)
    {
        return exception switch
        {
            HttpRequestException => $"{operation}に失敗しました。ネットワーク接続を確認してください。",
            UnauthorizedAccessException => $"{operation}に失敗しました。モデル保存先へアクセスできません。",
            IOException => $"{operation}に失敗しました。空き容量またはモデル保存先を確認してください。",
            InvalidDataException => $"{operation}に失敗しました。取得したモデルの完全性を確認できませんでした。",
            _ => $"{operation}に失敗しました。診断ログを確認してください。"
        };
    }

    private static void SelectComboBoxStringTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }
}
