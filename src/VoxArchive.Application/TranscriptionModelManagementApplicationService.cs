using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// ASRモデルの再確認・削除状態をPresentation向けDTOへ投影する
/// </summary>
public sealed class TranscriptionModelManagementApplicationService(
    TranscriptionModelManager modelManager) : ITranscriptionModelManagementApplicationService
{
    /// <inheritdoc />
    public TranscriptionModelManagementOperationInfo? GetActiveOperation()
    {
        var active = modelManager.GetActiveExclusiveOperation();
        return active is null
            ? null
            : new TranscriptionModelManagementOperationInfo(
                active.Key.EngineId.Value,
                active.Key.ModelId.Value,
                active.OperationName);
    }

    /// <inheritdoc />
    public Task WaitForActiveOperationAsync()
        => modelManager.WaitForActiveExclusiveOperationAsync();
}
