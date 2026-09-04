using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Windowの文字起こしタブとアプリケーション共有モデル管理を接続する
/// </summary>
public partial class SettingsWindow
{
    private bool _whisperTabVisited;
    private bool _reazonSpeechTabVisited;

    /// <summary>新規文字起こしで既定として使用するEngineの安定IDを取得・設定する</summary>
    public string DefaultTranscriptionEngine
    {
        get
        {
            if (DefaultEngineComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                return id;
            }

            return TranscriptionEngineId.Whisper.Value;
        }
        set
        {
            var normalized = string.Equals(value, TranscriptionEngineId.ReazonSpeech.Value, StringComparison.OrdinalIgnoreCase)
                ? TranscriptionEngineId.ReazonSpeech.Value
                : TranscriptionEngineId.Whisper.Value;
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
        PopulateModelChoices(TranscriptionEngineId.Whisper, WhisperModelManagerControl);
        PopulateModelChoices(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);

        WhisperModelManagerControl.SelectedModelChanged += OnWhisperModelSelectionChanged;
        WhisperModelManagerControl.VerifyRequested += OnWhisperModelVerifyRequested;
        WhisperModelManagerControl.InstallRequested += OnWhisperModelInstallRequested;
        WhisperModelManagerControl.DeleteRequested += OnWhisperModelDeleteRequested;

        ReazonSpeechModelManagerControl.SelectedModelChanged += OnReazonSpeechModelSelectionChanged;
        ReazonSpeechModelManagerControl.VerifyRequested += OnReazonSpeechModelVerifyRequested;
        ReazonSpeechModelManagerControl.InstallRequested += OnReazonSpeechModelInstallRequested;
        ReazonSpeechModelManagerControl.DeleteRequested += OnReazonSpeechModelDeleteRequested;

        _modelManager.StateChanged += OnModelManagerStateChanged;
        TranscriptionTabControl.SelectedIndex = 0;
    }

    private void PopulateModelChoices(TranscriptionEngineId engineId, TranscriptionModelManagerControl control)
    {
        control.Models.Clear();
        foreach (var model in _modelManager.GetAvailableModels(engineId))
        {
            control.Models.Add(new TranscriptionModelChoice(model.ModelId.Value, model.DisplayName));
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
            RefreshModelControl(TranscriptionEngineId.Whisper, WhisperModelManagerControl);
        }
        else if (TranscriptionTabControl.SelectedIndex == 2)
        {
            _reazonSpeechTabVisited = true;
            RefreshModelControl(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);
        }
    }

    private void OnWhisperModelSelectionChanged(object? sender, EventArgs e)
    {
        if (_whisperTabVisited)
        {
            RefreshModelControl(TranscriptionEngineId.Whisper, WhisperModelManagerControl);
        }

        SetDefaultEnvironmentStatus();
    }

    private void OnReazonSpeechModelSelectionChanged(object? sender, EventArgs e)
    {
        if (_reazonSpeechTabVisited)
        {
            RefreshModelControl(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);
        }
    }

    private void OnWhisperModelVerifyRequested(object? sender, EventArgs e)
        => _ = VerifyModelAsync(TranscriptionEngineId.Whisper, WhisperModelManagerControl);

    private void OnReazonSpeechModelVerifyRequested(object? sender, EventArgs e)
        => _ = VerifyModelAsync(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);

    private void OnWhisperModelInstallRequested(object? sender, EventArgs e)
        => _ = InstallOrCancelModelAsync(TranscriptionEngineId.Whisper, WhisperModelManagerControl);

    private void OnReazonSpeechModelInstallRequested(object? sender, EventArgs e)
        => _ = InstallOrCancelModelAsync(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);

    private void OnWhisperModelDeleteRequested(object? sender, EventArgs e)
        => _ = DeleteModelAsync(TranscriptionEngineId.Whisper, WhisperModelManagerControl);

    private void OnReazonSpeechModelDeleteRequested(object? sender, EventArgs e)
        => _ = DeleteModelAsync(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);

    private async Task VerifyModelAsync(TranscriptionEngineId engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        control.CanVerify = false;
        control.MessageText = "モデルファイルの完全性を確認しています...";
        try
        {
            var inspection = await _modelManager.VerifyAsync(engineId, modelId);
            if (inspection.State == TranscriptionModelPackageState.Installed)
            {
                control.StatusText = "取得済み";
                control.MessageText = "モデルファイルの完全性を確認しました。";
            }
            else
            {
                ApplyInspectionState(control, inspection.State);
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

    private async Task InstallOrCancelModelAsync(TranscriptionEngineId engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        var active = _modelManager.GetActiveDownload();
        if (active is not null && active.EngineId == engineId && active.ModelId == modelId)
        {
            if (active.WaiterCount > 0)
            {
                var result = ModernDialog.Show(
                    this,
                    "このモデルの取得完了を待っている文字起こしがあります。\nモデル取得を中止すると、待機中の文字起こしも中止されます。",
                    "モデル取得の中止",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
                if (result != MessageBoxResult.OK)
                {
                    return;
                }
            }

            _modelManager.CancelActiveDownload(engineId, modelId);
            return;
        }

        try
        {
            var inspection = _modelManager.Inspect(engineId, modelId, TranscriptionModelInspectionLevel.Existence);
            var force = inspection.State != TranscriptionModelPackageState.Missing;
            var participation = _modelManager.AcquireDownload(engineId, modelId, force);
            _ = ObserveSettingsDownloadAsync(participation, engineId, modelId, control);
            RefreshModelControl(engineId, control);
        }
        catch (TranscriptionModelDownloadBusyException ex)
        {
            control.MessageText = $"{ex.ActiveDownload.EngineId.Value} / {ex.ActiveDownload.ModelDisplayName} のモデルを取得中です。";
        }
        catch (Exception ex)
        {
            control.MessageText = BuildModelOperationErrorMessage("モデル取得", ex);
        }
    }

    private async Task ObserveSettingsDownloadAsync(
        TranscriptionModelDownloadParticipation participation,
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        TranscriptionModelManagerControl control)
    {
        try
        {
            await participation.Completion;
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
        finally
        {
            participation.Dispose();
        }
    }

    private async Task DeleteModelAsync(TranscriptionEngineId engineId, TranscriptionModelManagerControl control)
    {
        if (!TryGetSelectedModel(control, out var modelId))
        {
            return;
        }

        var displayName = GetModelDisplayName(engineId, modelId);
        var engineName = engineId == TranscriptionEngineId.Whisper ? "Whisper" : "ReazonSpeech";
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
            await _modelManager.DeleteAsync(engineId, modelId);
            control.MessageText = "モデルを削除しました。";
            RefreshModelControl(engineId, control, preserveMessage: true);
        }
        catch (Exception ex)
        {
            control.MessageText = BuildModelOperationErrorMessage("モデル削除", ex);
        }
    }

    private void RefreshModelControl(
        TranscriptionEngineId engineId,
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
            var inspection = _modelManager.Inspect(engineId, modelId, TranscriptionModelInspectionLevel.Existence);
            ApplyInspectionState(control, inspection.State);

            var isProtected = _modelManager.IsModelProtected(engineId, modelId);
            var active = _modelManager.GetActiveDownload();
            var isCurrentDownload = active is not null && active.EngineId == engineId && active.ModelId == modelId;

            if (isProtected)
            {
                control.StatusText = inspection.State == TranscriptionModelPackageState.Installed ? "使用中（取得済み）" : "使用中";
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
            control.CanDelete = inspection.State != TranscriptionModelPackageState.Missing;
            control.InstallButtonText = inspection.State == TranscriptionModelPackageState.Missing ? "モデル取得" : "モデル再取得";
            control.CanInstall = active is null;

            if (active is not null && !preserveMessage)
            {
                control.MessageText = $"{active.EngineId.Value} / {active.ModelDisplayName} のモデルを取得中です。";
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

    private static void ApplyInspectionState(TranscriptionModelManagerControl control, TranscriptionModelPackageState state)
    {
        control.StatusText = state switch
        {
            TranscriptionModelPackageState.Missing => "未取得",
            TranscriptionModelPackageState.Installed => "取得済み",
            TranscriptionModelPackageState.Incomplete => "不完全",
            TranscriptionModelPackageState.Corrupt => "破損または不完全",
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
            RefreshModelControl(TranscriptionEngineId.Whisper, WhisperModelManagerControl);
        }

        if (_reazonSpeechTabVisited)
        {
            RefreshModelControl(TranscriptionEngineId.ReazonSpeech, ReazonSpeechModelManagerControl);
        }
    }

    private static bool TryGetSelectedModel(TranscriptionModelManagerControl control, out TranscriptionModelId modelId)
    {
        if (!string.IsNullOrWhiteSpace(control.SelectedModelId))
        {
            modelId = new TranscriptionModelId(control.SelectedModelId);
            return true;
        }

        modelId = default;
        return false;
    }

    private string GetModelDisplayName(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        return _modelManager.GetAvailableModels(engineId)
            .FirstOrDefault(model => model.ModelId == modelId)?.DisplayName ?? modelId.Value;
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
