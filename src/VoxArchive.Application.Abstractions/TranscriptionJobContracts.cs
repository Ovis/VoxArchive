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
/// 1回の文字起こしジョブの完了結果を表す
/// </summary>
public sealed record TranscriptionJobResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string> GeneratedFiles,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

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
