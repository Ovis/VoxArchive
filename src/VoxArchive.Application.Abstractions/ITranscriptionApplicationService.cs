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
    /// <summary>文字起こしジョブが完了したときに通知する</summary>
    event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;

    /// <summary>文字起こしジョブ状態が変化したときに通知する</summary>
    event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;

    /// <summary>モデル取得・削除・進捗などの状態が変化したときに通知する</summary>
    event EventHandler? ModelStateChanged;

    /// <summary>現在設定をsnapshotして文字起こしQueueへ投入する</summary>
    Task<TranscriptionEnqueueResult> TryEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionTrigger trigger,
        CancellationToken cancellationToken = default);

    /// <summary>Engineが公開するモデル一覧を取得する</summary>
    IReadOnlyList<TranscriptionModelInfo> GetAvailableModels(string engineId);

    /// <summary>軽量なモデル配置状態を取得する</summary>
    TranscriptionModelStatusInfo InspectModel(string engineId, string modelId);

    /// <summary>SHA-256を含むモデル完全性を明示的に確認する</summary>
    Task<TranscriptionModelStatusInfo> ReverifyModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>モデルを取得または再取得する</summary>
    Task InstallModelAsync(
        string engineId,
        string modelId,
        bool force,
        IProgress<TranscriptionModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>指定モデルを削除する</summary>
    Task DeleteModelAsync(
        string engineId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>指定モデルがqueued/running Jobから保護されているか確認する</summary>
    bool IsModelProtected(string engineId, string modelId);

    /// <summary>現在のモデル取得状態を取得する</summary>
    TranscriptionModelDownloadInfo? GetActiveModelDownload();

    /// <summary>現在進行中の指定モデル取得へキャンセルを通知する</summary>
    bool CancelModelDownload(string engineId, string modelId);

    /// <summary>アプリ終了時に進行中モデル取得をキャンセルし、終了まで待つ</summary>
    Task CancelActiveModelDownloadAndWaitAsync();

    /// <summary>Engine固有診断をUI向けDTOとして取得する</summary>
    Task<IReadOnlyList<TranscriptionDiagnosticInfo>> DiagnoseEngineAsync(
        string engineId,
        CancellationToken cancellationToken = default);
}

/// <summary>Queue投入結果をUIへ公開する</summary>
public sealed record TranscriptionEnqueueResult(bool Enqueued, string Message);

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
public sealed record TranscriptionModelDownloadInfo(
    string EngineId,
    string ModelId,
    string ModelDisplayName,
    long BytesReceived,
    long TotalBytes,
    int WaiterCount,
    bool IsCancelling)
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
