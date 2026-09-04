using System.Windows;
using Microsoft.Extensions.Logging;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしジョブの実行前に、スナップショットされたEngine/Modelが利用可能か確認する
/// </summary>
/// <remarks>
/// 手動実行ではユーザー確認のうえモデルを取得し、自動実行では新しい取得を暗黙に開始しない。
/// すでに同一モデルを取得中の場合だけ既存処理へ参加し、取得完了後に元のジョブを継続する。
/// </remarks>
public sealed class TranscriptionModelRequirementService
{
    private readonly TranscriptionModelManager _modelManager;
    private readonly ILogger<TranscriptionModelRequirementService> _logger;

    /// <summary>モデル保証サービスを初期化する</summary>
    public TranscriptionModelRequirementService(
        TranscriptionModelManager modelManager,
        ILogger<TranscriptionModelRequirementService> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    /// <summary>
    /// Requestが参照するモデルを実行可能な状態へ準備する
    /// </summary>
    /// <returns>実行可能なら成功、スキップまたはキャンセルすべき場合は理由を含む失敗結果</returns>
    public async Task<TranscriptionModelRequirementResult> EnsureReadyAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_modelManager.IsReadyForExecution(request.EngineId, request.ModelId))
        {
            return TranscriptionModelRequirementResult.ReadyResult;
        }

        var active = _modelManager.GetActiveDownload();
        var sameDownload = active is not null
            && active.EngineId == request.EngineId
            && active.ModelId == request.ModelId;

        if (request.Trigger == TranscriptionTrigger.AutoAfterRecord)
        {
            if (!sameDownload)
            {
                var message = BuildUnavailableMessage(request, "モデルが未取得または不完全なため、自動文字起こしをスキップしました。");
                _logger.LogWarning(
                    "Auto transcription skipped because model is unavailable. Engine={Engine}, Model={Model}, File={File}",
                    request.EngineId,
                    request.ModelId,
                    request.AudioFilePath);
                NotifyModelUnavailable(message);
                return new TranscriptionModelRequirementResult(false, message);
            }

            return await WaitForExistingDownloadAsync(request, showProgressWindow: false, cancellationToken);
        }

        if (active is not null && !sameDownload)
        {
            var message = $"現在 {active.EngineId.Value} / {active.ModelDisplayName} のモデルを取得中です。別のモデルを同時に取得することはできません。取得完了後にもう一度文字起こしを実行してください。";
            ShowMessage(message, "モデル取得中", MessageBoxImage.Information);
            return new TranscriptionModelRequirementResult(false, message);
        }

        if (!sameDownload)
        {
            var displayName = GetModelDisplayName(request.EngineId, request.ModelId);
            var engineName = GetEngineDisplayName(request.EngineId);
            var result = ShowConfirmation(
                $"{engineName} のモデル「{displayName}」が利用できる状態ではありません。\n\nモデルを取得して文字起こしを開始しますか？",
                "モデル取得");
            if (result != MessageBoxResult.OK)
            {
                return new TranscriptionModelRequirementResult(false, "モデル取得がキャンセルされたため、文字起こしを開始しませんでした。");
            }
        }

        return await WaitForExistingDownloadAsync(request, showProgressWindow: true, cancellationToken);
    }

    private async Task<TranscriptionModelRequirementResult> WaitForExistingDownloadAsync(
        TranscriptionJobRequest request,
        bool showProgressWindow,
        CancellationToken cancellationToken)
    {
        TranscriptionModelDownloadParticipation participation;
        try
        {
            var inspection = _modelManager.Inspect(request.EngineId, request.ModelId, TranscriptionModelInspectionLevel.Existence);
            var force = inspection.State != TranscriptionModelPackageState.Missing;
            participation = _modelManager.AcquireDownload(
                request.EngineId,
                request.ModelId,
                force,
                allowProtectedModel: true);
        }
        catch (TranscriptionModelDownloadBusyException ex)
        {
            var message = $"現在 {ex.ActiveDownload.EngineId.Value} / {ex.ActiveDownload.ModelDisplayName} のモデルを取得中です。";
            if (request.Trigger == TranscriptionTrigger.AutoAfterRecord)
            {
                NotifyModelUnavailable(message);
            }
            else
            {
                ShowMessage(message, "モデル取得中", MessageBoxImage.Information);
            }
            return new TranscriptionModelRequirementResult(false, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start or join transcription model download. Engine={Engine}, Model={Model}", request.EngineId, request.ModelId);
            return new TranscriptionModelRequirementResult(false, $"モデル取得を開始できませんでした: {ex.Message}");
        }

        var localCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TranscriptionModelDownloadProgressWindow? progressWindow = null;

        try
        {
            if (showProgressWindow)
            {
                progressWindow = ShowProgressWindow(request, participation, localCancellation);
            }

            using var cancellationRegistration = cancellationToken.Register(() => localCancellation.TrySetResult());
            var completed = await Task.WhenAny(participation.Completion, localCancellation.Task);
            if (completed == localCancellation.Task)
            {
                if (!participation.StartedDownload)
                {
                    participation.Dispose();
                }
                return new TranscriptionModelRequirementResult(false, "モデル取得の待機をキャンセルしたため、文字起こしを開始しませんでした。");
            }

            await participation.Completion;
            if (!_modelManager.IsReadyForExecution(request.EngineId, request.ModelId))
            {
                return new TranscriptionModelRequirementResult(false, "モデル取得後のサイズ確認に失敗しました。設定画面からモデルを再取得してください。");
            }

            return TranscriptionModelRequirementResult.ReadyResult;
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionModelRequirementResult(false, "モデル取得がキャンセルされたため、文字起こしを開始しませんでした。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription model download failed before job execution. Engine={Engine}, Model={Model}", request.EngineId, request.ModelId);
            var message = $"モデル取得に失敗したため、文字起こしを開始できませんでした: {ex.Message}";
            if (request.Trigger == TranscriptionTrigger.AutoAfterRecord)
            {
                NotifyModelUnavailable(message);
            }
            return new TranscriptionModelRequirementResult(false, message);
        }
        finally
        {
            participation.Dispose();
            if (progressWindow is not null)
            {
                CloseProgressWindow(progressWindow);
            }
        }
    }

    private TranscriptionModelDownloadProgressWindow? ShowProgressWindow(
        TranscriptionJobRequest request,
        TranscriptionModelDownloadParticipation participation,
        TaskCompletionSource localCancellation)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Dispatcher.Invoke(() =>
        {
            var window = new TranscriptionModelDownloadProgressWindow(
                _modelManager,
                request.EngineId,
                request.ModelId,
                participation.StartedDownload,
                () =>
                {
                    if (!participation.StartedDownload)
                    {
                        localCancellation.TrySetResult();
                        return;
                    }

                    var active = _modelManager.GetActiveDownload();
                    if (active is not null && active.WaiterCount > 0)
                    {
                        var confirmation = ModernDialog.Show(
                            window,
                            "このモデルの取得完了を待っている文字起こしがあります。\nモデル取得を中止すると、待機中の文字起こしも中止されます。",
                            "モデル取得の中止",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning,
                            MessageBoxResult.Cancel);
                        if (confirmation != MessageBoxResult.OK)
                        {
                            return;
                        }
                    }

                    _modelManager.CancelActiveDownload(request.EngineId, request.ModelId);
                })
            {
                Owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow
            };
            window.Show();
            return window;
        });
    }

    private static void CloseProgressWindow(TranscriptionModelDownloadProgressWindow window)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        _ = app.Dispatcher.BeginInvoke(() => window.CloseAfterCompletion());
    }

    private string GetModelDisplayName(TranscriptionEngineId engineId, TranscriptionModelId modelId)
        => _modelManager.GetAvailableModels(engineId)
            .FirstOrDefault(model => model.ModelId == modelId)?.DisplayName ?? modelId.Value;

    private static string GetEngineDisplayName(TranscriptionEngineId engineId)
        => engineId == TranscriptionEngineId.ReazonSpeech ? "ReazonSpeech" : "Whisper";

    private string BuildUnavailableMessage(TranscriptionJobRequest request, string detail)
        => $"{detail}\n{GetEngineDisplayName(request.EngineId)} / {GetModelDisplayName(request.EngineId, request.ModelId)}";

    private static MessageBoxResult ShowConfirmation(string message, string title)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return MessageBoxResult.Cancel;
        }

        return app.Dispatcher.Invoke(() => ModernDialog.Show(
            app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow,
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel));
    }

    private static void ShowMessage(string message, string title, MessageBoxImage icon)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        _ = app.Dispatcher.Invoke(() => ModernDialog.Show(
            app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
    }

    private static void NotifyModelUnavailable(string message)
        => AppNotificationHub.Notify("VoxArchive", message, System.Windows.Forms.ToolTipIcon.Warning);
}

/// <summary>文字起こし実行前のモデル保証結果を表す</summary>
/// <param name="Ready">モデルが実行可能ならtrue</param>
/// <param name="Message">失敗時にジョブ結果へ記録する理由</param>
public sealed record TranscriptionModelRequirementResult(bool Ready, string Message)
{
    /// <summary>モデルが実行可能な場合の共通結果</summary>
    public static TranscriptionModelRequirementResult ReadyResult { get; } = new(true, string.Empty);
}
