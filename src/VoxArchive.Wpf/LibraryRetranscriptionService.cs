using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// Libraryで選択した文字起こし結果を、保存済み条件を基準に再度Queueへ投入する
/// </summary>
public sealed class LibraryRetranscriptionService(
    TranscriptionJobQueue transcriptionQueue,
    ISettingsService settingsService)
{
    /// <summary>
    /// 再文字起こし用Requestを準備する
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
    /// 準備済みの再文字起こしRequestを既存Queueへ投入する
    /// </summary>
    public bool TryEnqueue(TranscriptionJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return transcriptionQueue.TryEnqueue(request);
    }
}
