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

    /// <summary>
    /// 手動Admissionで不足したモデルについて利用者確認が完了した後、モデル取得と再Admissionを行う
    /// </summary>
    /// <remarks>
    /// UIは確認と進捗表示だけを担当し、download完了待ちと再Queue投入のpolicyはApplicationへ集約する。
    /// 確認後に必要モデルが変化した場合は、未確認の別モデルを暗黙に取得せずMissingModelを返す。
    /// </remarks>
    Task<TranscriptionEnqueueResult> DownloadMissingModelAndRetryManualEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionMissingModelInfo confirmedModel,
        IProgress<TranscriptionModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default);
    bool CancelJob(string audioFilePath);
    IReadOnlyList<TranscriptionJobStateInfo> GetJobStates();
    string? FindCanonicalResultPath(string audioFilePath, RecordingOptions recordingOptions);

    Task<IReadOnlyList<TranscriptionResultInfo>> DiscoverResultsAsync(string audioFilePath, CancellationToken cancellationToken = default);
    Task<TranscriptionResultDocumentInfo> LoadResultAsync(string documentPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存済みcanonical resultのEngine/Modelを現在設定へ適用し、再文字起こし用の設定snapshotを生成する
    /// </summary>
    Task<TranscriptionRetranscriptionPreparation> PrepareRetranscriptionAsync(
        string documentPath,
        RecordingOptions currentOptions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ExportResultAsync(string documentPath, TranscriptionOutputFormats formats, CancellationToken cancellationToken = default);
    Task DeleteResultAsync(string documentPath, CancellationToken cancellationToken = default);

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

/// <summary>
/// Queue投入結果と、利用者操作が必要な場合の不足モデル情報を表す
/// </summary>
public sealed record TranscriptionEnqueueResult(
    bool Enqueued,
    string Message,
    TranscriptionMissingModelInfo? MissingModel = null);

/// <summary>手動実行を続行するため取得が必要なモデルを表す</summary>
public sealed record TranscriptionMissingModelInfo(string EngineId, string ModelId, string DisplayName);

/// <summary>UIへ公開する文字起こしJobの現在状態を表す</summary>
public sealed record TranscriptionJobStateInfo(string AudioFilePath, TranscriptionJobState State);

/// <summary>Engineが利用可能として公開するモデルを表す</summary>
public sealed record TranscriptionModelInfo(string Id, string DisplayName);

/// <summary>モデルpackageの検査状態と実行可能性を表す</summary>
public sealed record TranscriptionModelStatusInfo(string State, bool IsReady);

/// <summary>録音ファイルに紐づくcanonical文字起こし結果の概要を表す</summary>
public sealed record TranscriptionResultInfo(
    string DocumentPath,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt);

/// <summary>canonical文字起こし結果をUI表示用に投影したdocumentを表す</summary>
public sealed record TranscriptionResultDocumentInfo(
    string DocumentPath,
    string SourceFileName,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptionResultSegmentInfo> Segments);

/// <summary>UIへ公開する文字起こしsegmentを表す</summary>
public sealed record TranscriptionResultSegmentInfo(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string? Speaker);

/// <summary>再文字起こし用の設定snapshotと現在設定による補完有無を表す</summary>
public sealed record TranscriptionRetranscriptionPreparation(
    RecordingOptions Options,
    bool UsedCurrentSettingsFallback);

/// <summary>モデル取得の転送量と進捗率を表す</summary>
public sealed record TranscriptionModelTransferInfo(long BytesReceived, long TotalBytes)
{
    /// <summary>0から100の範囲へ正規化した取得進捗率を返す</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>現在進行中のモデル取得状態をUIへ公開する</summary>
public sealed record TranscriptionModelDownloadInfo(string EngineId, string ModelId, string ModelDisplayName, long BytesReceived, long TotalBytes, int WaiterCount, bool IsCancelling)
{
    /// <summary>0から100の範囲へ正規化した取得進捗率を返す</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>Engine診断結果をUIへ公開する</summary>
public sealed record TranscriptionDiagnosticInfo(string Code, string Message, TranscriptionDiagnosticLevel Level);

/// <summary>Engine診断結果の重大度を表す</summary>
public enum TranscriptionDiagnosticLevel
{
    Information = 0,
    Warning = 1,
    Error = 2,
}
