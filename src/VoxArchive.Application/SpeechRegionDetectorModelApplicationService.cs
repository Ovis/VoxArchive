using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// 共通発話検出モデル管理をPresentation向けDTOへ投影する
/// </summary>
/// <remarks>
/// ApplicationはSilero具象型を知らず、Transcription層の共通モデル管理契約だけに依存する。
/// これにより将来detector実装を差し替えてもWPFの操作契約を変更せずに済む。
/// </remarks>
public sealed class SpeechRegionDetectorModelApplicationService(
    ISpeechRegionDetectorModelManager modelManager) : ISpeechRegionDetectorModelApplicationService
{
    /// <inheritdoc />
    public SpeechRegionDetectorModelStatusInfo Inspect()
        => ToStatus(modelManager.GetState());

    /// <inheritdoc />
    public Task<SpeechRegionDetectorModelStatusInfo> ReverifyAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ToStatus(modelManager.Recheck());
        }, cancellationToken);

    /// <inheritdoc />
    public Task InstallAsync(
        bool force,
        IProgress<SpeechRegionDetectorModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = progress is null
            ? null
            : new Progress<ManagedModelTransactionProgress>(x => progress.Report(
                new SpeechRegionDetectorModelTransferInfo(
                    x.BytesReceived,
                    x.TotalBytes,
                    x.CurrentFileName,
                    x.IsValidating)));
        return modelManager.InstallAsync(force, adapter, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            modelManager.Delete();
        }, cancellationToken);

    private static SpeechRegionDetectorModelStatusInfo ToStatus(SpeechRegionDetectorModelState state)
        => new(
            state.ToString(),
            state == SpeechRegionDetectorModelState.Available);
}
