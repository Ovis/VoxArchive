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

    /// <summary>
    /// opaqueなEngine settingsをUI向けの共通設定値へ投影する
    /// </summary>
    TranscriptionEngineConfigurationInfo GetEngineConfiguration(string engineId, TranscriptionEngineSettings settings);

    /// <summary>
    /// UIで選択された共通設定値をEngine capability経由でopaque settingsへ反映する
    /// </summary>
    TranscriptionEngineSettings UpdateEngineConfiguration(
        string engineId,
        TranscriptionEngineSettings settings,
        string? modelId,
        string? executionModeId);

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
/// 手動文字起こしで必要なモデルが未配置だった場合に、利用者へ取得確認を求めるUI portを定義する
/// </summary>
public interface ITranscriptionModelDownloadConfirmation
{
    Task<bool> ConfirmAsync(TranscriptionMissingModelInfo model, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionEnqueueResult(bool Enqueued, string Message);
public sealed record TranscriptionMissingModelInfo(string EngineId, string ModelId, string DisplayName);
public sealed record TranscriptionJobStateInfo(string AudioFilePath, TranscriptionJobState State);
public sealed record TranscriptionModelInfo(string Id, string DisplayName);
public sealed record TranscriptionModelStatusInfo(string State, bool IsReady);

/// <summary>
/// Engine固有settingsをUIがJSONとして解釈せずに表示するための共通投影を表す
/// </summary>
public sealed record TranscriptionEngineConfigurationInfo(
    string? ModelId,
    string? ExecutionModeId,
    IReadOnlyList<TranscriptionExecutionModeInfo> ExecutionModes);

/// <summary>Engineが公開する実行方式の安定IDと表示名を表す</summary>
public sealed record TranscriptionExecutionModeInfo(string Id, string DisplayName);

public sealed record TranscriptionResultInfo(
    string DocumentPath,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt);

public sealed record TranscriptionResultDocumentInfo(
    string DocumentPath,
    string SourceFileName,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptionResultSegmentInfo> Segments);

public sealed record TranscriptionResultSegmentInfo(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string? Speaker);

/// <summary>再文字起こし用の設定snapshotと現在設定による補完有無を表す</summary>
public sealed record TranscriptionRetranscriptionPreparation(
    RecordingOptions Options,
    bool UsedCurrentSettingsFallback);

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
