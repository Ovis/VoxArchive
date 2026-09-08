using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Application.Abstractions;

/// <summary>
/// 文字起こしジョブを開始した契機を表す
/// </summary>
public enum TranscriptionTrigger
{
    Manual = 0,
    AutoAfterRecord = 1,
}

/// <summary>
/// 文字起こしジョブの待機・実行状態を表す
/// </summary>
public enum TranscriptionJobState
{
    Pending = 0,
    Running = 1,
}

/// <summary>
/// Queueへ投入された文字起こしジョブのterminal outcomeを表す
/// </summary>
public enum TranscriptionJobOutcome
{
    Completed = 0,
    Cancelled = 1,
    Failed = 2,
}

/// <summary>
/// 1回の文字起こしジョブの完了結果を表す
/// </summary>
public sealed record TranscriptionJobResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string> GeneratedFiles,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    /// <summary>
    /// Queue境界で確定したterminal outcomeを取得する
    /// </summary>
    public TranscriptionJobOutcome Outcome { get; init; }
        = Succeeded ? TranscriptionJobOutcome.Completed : TranscriptionJobOutcome.Failed;

    /// <summary>
    /// VADが正常終了したものの発話区間を1件も検出しなかったかを取得する
    /// </summary>
    /// <remarks>
    /// ASRが空結果を返したケースとは区別し、Presentation層が「音声区間なし」を正確に通知できるよう構造化して保持する。
    /// </remarks>
    public bool NoSpeechDetected { get; init; }
}

/// <summary>
/// Queue投入後に固定される文字起こしジョブの識別情報を表す
/// </summary>
public sealed record TranscriptionJobDescriptor(
    string AudioFilePath,
    TranscriptionEngineId EngineId,
    TranscriptionModelId? ModelId,
    TranscriptionTrigger Trigger,
    bool DiagnosticsEnabled);

/// <summary>
/// 文字起こしジョブ完了イベントの情報を表す
/// </summary>
public sealed record TranscriptionJobCompletedEventArgs(
    TranscriptionJobDescriptor Job,
    TranscriptionJobResult Result);

/// <summary>
/// 文字起こしジョブ状態のsnapshotを表す
/// </summary>
public sealed record TranscriptionJobStateSnapshot(
    string AudioFilePath,
    TranscriptionJobState State);

/// <summary>
/// 文字起こしジョブ状態変更イベントの情報を表す
/// </summary>
public sealed record TranscriptionJobStateChangedEventArgs(
    string AudioFilePath,
    TranscriptionJobState? State);
