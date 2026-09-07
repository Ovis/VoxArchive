using System.IO;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// Libraryで選択した文字起こし結果をApplication Use Caseへ再投入する
/// </summary>
public sealed class LibraryRetranscriptionService(
    ITranscriptionApplicationService transcriptionApplicationService,
    ISettingsService settingsService)
{
    /// <summary>
    /// 保存済みcanonical resultと現在設定から再文字起こし用snapshotを準備する
    /// </summary>
    public async Task<TranscriptionRetranscriptionPreparation> PrepareAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        var currentOptions = await settingsService.LoadRecordingOptionsAsync(cancellationToken);
        if (!currentOptions.Transcription.Enabled)
        {
            throw new InvalidOperationException("文字起こし機能が無効です。設定画面で有効化してください。");
        }

        return await transcriptionApplicationService.PrepareRetranscriptionAsync(
            documentPath,
            currentOptions,
            cancellationToken);
    }

    /// <summary>
    /// 準備済みの設定snapshotを通常の手動文字起こしとしてApplicationへ投入する
    /// </summary>
    public async Task<TranscriptionEnqueueResult> EnqueueAsync(
        string audioFilePath,
        TranscriptionRetranscriptionPreparation prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(prepared);

        var result = await transcriptionApplicationService.TryEnqueueAsync(
            audioFilePath,
            prepared.Options,
            TranscriptionTrigger.Manual,
            cancellationToken);

        if (result.Enqueued && prepared.Options.Transcription.ToastNotificationEnabled)
        {
            AppNotificationHub.Notify(
                "VoxArchive",
                $"文字起こし開始: {Path.GetFileName(audioFilePath)}",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        return result;
    }
}
