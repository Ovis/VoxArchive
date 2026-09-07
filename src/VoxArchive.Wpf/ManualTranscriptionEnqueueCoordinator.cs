using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 手動文字起こしの利用者確認とモデル取得進捗UIをApplication Use Caseへ接続する
/// </summary>
/// <remarks>
/// MissingModelの判定、download完了待ち、再AdmissionはApplicationが所有する。
/// 本クラスは利用者の承認とWindow表示だけを担当し、通常実行と再文字起こしで同じPresentation flowを共有する。
/// </remarks>
public sealed class ManualTranscriptionEnqueueCoordinator(ITranscriptionApplicationService transcriptionService)
{
    /// <summary>
    /// 手動文字起こしを要求し、不足モデルがある場合は確認後にApplicationへ継続を委譲する
    /// </summary>
    public async Task<TranscriptionEnqueueResult> TryEnqueueAsync(
        string audioFilePath,
        RecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(options);

        var result = await transcriptionService.TryEnqueueAsync(
            audioFilePath,
            options,
            TranscriptionTrigger.Manual,
            cancellationToken);
        if (result.Enqueued || result.MissingModel is null)
        {
            return result;
        }

        var missingModel = result.MissingModel;
        var confirmation = ModernDialog.Show(
            $"文字起こしに必要なモデル「{missingModel.DisplayName}」が取得されていません。
モデルを取得して文字起こしを続行しますか？",
            "文字起こしモデル未取得",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel);
        if (confirmation != System.Windows.MessageBoxResult.OK)
        {
            return new TranscriptionEnqueueResult(
                false,
                "モデル取得がキャンセルされたため文字起こしを開始しませんでした。");
        }

        var progressWindow = new TranscriptionModelDownloadProgressWindow(
            missingModel,
            () => transcriptionService.CancelModelDownload(missingModel.EngineId, missingModel.ModelId))
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        var progress = new Progress<TranscriptionModelTransferInfo>(progressWindow.Report);

        try
        {
            // Application側が再Admissionしてから必要な場合だけdownloadを開始する。
            // すでにモデルがreadyになっていた場合はWindowを一瞬表示しない。
            var continuation = transcriptionService.DownloadMissingModelAndRetryManualEnqueueAsync(
                audioFilePath,
                options,
                missingModel,
                progress,
                cancellationToken);

            if (!continuation.IsCompleted)
            {
                progressWindow.Show();
                var activeDownload = transcriptionService.GetActiveModelDownload();
                if (activeDownload is not null
                    && string.Equals(activeDownload.EngineId, missingModel.EngineId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activeDownload.ModelId, missingModel.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    progressWindow.Report(new TranscriptionModelTransferInfo(
                        activeDownload.BytesReceived,
                        activeDownload.TotalBytes));
                }
            }

            return await continuation;
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionEnqueueResult(
                false,
                "モデル取得がキャンセルされたため文字起こしを開始しませんでした。");
        }
        finally
        {
            progressWindow.CloseAfterCompletion();
        }
    }
}
