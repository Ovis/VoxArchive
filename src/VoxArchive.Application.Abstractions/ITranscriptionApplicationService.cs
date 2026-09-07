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

    /// <summary>指定録音に対応する現行canonical resultの一覧を取得する</summary>
    Task<IReadOnlyList<TranscriptionResultInfo>> DiscoverResultsAsync(string audioFilePath, CancellationToken cancellationToken = default);

    /// <summary>指定canonical resultをUI向けdocumentとして読み込む</summary>
    Task<TranscriptionResultDocumentInfo> LoadResultAsync(string documentPath, CancellationToken cancellationToken = default);

    /// <summary>canonical resultから選択形式の派生ファイルを再生成する</summary>
    Task<IReadOnlyList<string>> ExportResultAsync(string documentPath, TranscriptionOutputFormats formats, CancellationToken cancellationToken = default);

    /// <summary>canonical JSONだけを削除する。ユーザー編集の可能性がある派生ファイルは残す</summary>
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
/// 手動文字起こしで必要なモデルが未配置だった場合に、利用者へ取得確認を求めるUI portを定義する
/// </summary>
public interface ITranscriptionModelDownloadConfirmation
{
    /// <summary>指定モデルを取得してから文字起こしを続行してよいか確認する</summary>
    Task<bool> ConfirmAsync(TranscriptionMissingModelInfo model, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionEnqueueResult(bool Enqueued, string Message);
public sealed record TranscriptionMissingModelInfo(string EngineId, string ModelId, string DisplayName);
public sealed record TranscriptionJobStateInfo(string AudioFilePath, TranscriptionJobState State);
public sealed record TranscriptionModelInfo(string Id, string DisplayName);
public sealed record TranscriptionModelStatusInfo(string State, bool IsReady);

/// <summary>Libraryで選択可能なcanonical resultの概要を表す</summary>
public sealed record TranscriptionResultInfo(
    string DocumentPath,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt);

/// <summary>Libraryで表示するcanonical result本文を表す</summary>
public sealed record TranscriptionResultDocumentInfo(
    string DocumentPath,
    string SourceFileName,
    string EngineId,
    string? ModelId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptionResultSegmentInfo> Segments);

/// <summary>Library表示用の1認識segmentを表す</summary>
public sealed record TranscriptionResultSegmentInfo(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string? Speaker);

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
