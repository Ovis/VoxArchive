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

    /// <summary>現在設定をsnapshotして文字起こしQueueへ投入する</summary>
    Task<TranscriptionEnqueueResult> TryEnqueueAsync(string audioFilePath, RecordingOptions recordingOptions, TranscriptionTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>現在の待機中・実行中ジョブ一覧をUI向けsnapshotとして取得する</summary>
    IReadOnlyList<TranscriptionJobStateInfo> GetJobStates();

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

/// <summary>Queue投入結果をUIへ公開する</summary>
public sealed record TranscriptionEnqueueResult(bool Enqueued, string Message);

/// <summary>待機中・実行中ジョブ状態をUIへ公開する</summary>
public sealed record TranscriptionJobStateInfo(string AudioFilePath, TranscriptionJobState State);

/// <summary>モデル選択肢をUIへ公開する</summary>
public sealed record TranscriptionModelInfo(string Id, string DisplayName);

/// <summary>モデル配置状態をUIへ公開する</summary>
public sealed record TranscriptionModelStatusInfo(string State, bool IsReady);

/// <summary>モデル転送進捗をUIへ公開する</summary>
public sealed record TranscriptionModelTransferInfo(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>進行中のモデル取得状態をUIへ公開する</summary>
public sealed record TranscriptionModelDownloadInfo(string EngineId, string ModelId, string ModelDisplayName, long BytesReceived, long TotalBytes, int WaiterCount, bool IsCancelling)
{
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>Engine診断項目をUIへ公開する</summary>
public sealed record TranscriptionDiagnosticInfo(string Code, string Message, TranscriptionDiagnosticLevel Level);

/// <summary>Engine診断項目の重大度をUI層向けに定義する</summary>
public enum TranscriptionDiagnosticLevel
{
    Information = 0,
    Warning = 1,
    Error = 2,
}
