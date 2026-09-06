using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFから文字起こしUse Caseを操作するためのEngine非依存Facadeを定義する
/// </summary>
/// <remarks>
/// UIがTranscription CoreやEngine具象型へ依存しないため、識別子と状態はUI向けDTOへ投影して公開する。
/// </remarks>
public interface ITranscriptionApplicationService
{
    event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;
    event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;
    event EventHandler? ModelStateChanged;

    Task<TranscriptionEnqueueResult> TryEnqueueAsync(string audioFilePath, RecordingOptions recordingOptions, TranscriptionTrigger trigger, CancellationToken cancellationToken = default);
    IReadOnlyList<TranscriptionJobStateInfo> GetJobStates();

    /// <summary>現在設定に対応するcanonical JSONのパスを返す。存在しない場合はnullを返す</summary>
    string? FindCanonicalResultPath(string audioFilePath, RecordingOptions recordingOptions);

    IReadOnlyList<TranscriptionModelInfo> GetAvailableModels(string engineId);
    TranscriptionModelStatusInfo InspectModel(string engineId, string modelId);
    Task<TranscriptionModelStatusInfo> ReverifyModelAsync(string engineId, string modelId, CancellationToken cancellationToken = default);
    Task InstallModelAsync(string engineId, string modelId, bool force, IProgress<TranscriptionModelTransferInfo>? progress = null, CancellationToken cancellationToken = default);
    Task DeleteModelAsync(string engineId, string modelId, CancellationToken cancellationToken = default);
    bool IsModelProtected(string engineId, string modelId);
    TranscriptionModelDownloadInfo? GetActiveModelDownload();
    bool CancelModelDownload(string engineId, string modelId);
    Task CancelActiveModelDownloadAndWaitAsync();
    Task<IReadOnlyList<TranscriptionDiagnosticInfo>> DiagnoseEngineAsync(string engineId, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionEnqueueResult(bool Enqueued, string Message);
public sealed record TranscriptionJobStateInfo(string AudioFilePath, TranscriptionJobState State);
public sealed record TranscriptionModelInfo(string Id, string DisplayName);
public sealed record TranscriptionModelStatusInfo(string State, bool IsReady);
public sealed record TranscriptionModelTransferInfo(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}
public sealed record TranscriptionModelDownloadInfo(string EngineId, string ModelId, string ModelDisplayName, long BytesReceived, long TotalBytes, int WaiterCount, bool IsCancelling)
{
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}
public sealed record TranscriptionDiagnosticInfo(string Code, string Message, TranscriptionDiagnosticLevel Level);
public enum TranscriptionDiagnosticLevel { Information = 0, Warning = 1, Error = 2 }
