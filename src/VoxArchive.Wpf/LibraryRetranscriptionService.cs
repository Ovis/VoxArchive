using System.IO;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Libraryで選択した文字起こし結果を、保存済み条件を基準にApplication Use Caseへ再投入する
/// </summary>
public sealed class LibraryRetranscriptionService(
    ITranscriptionApplicationService transcriptionApplicationService,
    ISettingsService settingsService)
{
    /// <summary>
    /// 再文字起こし用の設定snapshotを準備する
    /// </summary>
    /// <remarks>
    /// canonical documentに保存されているEngine/Model/requested optionsを優先し、
    /// 保存されていない設定だけを現在の永続設定から補完する。
    /// </remarks>
    public async Task<RetranscriptionRequestBuildResult> PrepareAsync(
        string audioFilePath,
        VoxArchive.Domain.TranscriptionDocument document,
        bool isLegacy,
        CancellationToken cancellationToken = default)
    {
        var currentOptions = await settingsService.LoadRecordingOptionsAsync(cancellationToken);
        if (!currentOptions.TranscriptionEnabled)
        {
            throw new InvalidOperationException("文字起こし機能が無効です。設定画面で有効化してください。");
        }

        return TranscriptionRetranscriptionRequestFactory.Create(
            audioFilePath,
            document,
            currentOptions,
            isLegacy);
    }

    /// <summary>
    /// 準備済みの設定snapshotを通常の手動文字起こしとしてApplicationへ投入する
    /// </summary>
    public async Task<TranscriptionEnqueueResult> EnqueueAsync(
        string audioFilePath,
        RetranscriptionRequestBuildResult prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(prepared);

        var result = await transcriptionApplicationService.TryEnqueueAsync(
            audioFilePath,
            prepared.Options,
            TranscriptionTrigger.Manual,
            cancellationToken);

        if (result.Enqueued && prepared.Options.TranscriptionToastNotificationEnabled)
        {
            // 通常のLibrary手動実行と同じ開始通知を維持する。
            AppNotificationHub.Notify(
                "VoxArchive",
                $"文字起こし開始: {Path.GetFileName(audioFilePath)}",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        return result;
    }
}
