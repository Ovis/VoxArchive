using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 1つのRecognitionChunkについて、境界と分割理由を診断用に保持する
/// </summary>
public sealed record RecognitionChunkDiagnosticTrace(
    int RecognitionChunkId,
    int SpeechRegionId,
    long StartSample,
    long EndSample,
    string SplitReason,
    JsonElement? SplitDetails = null);

/// <summary>
/// Chunkerが生成したRecognitionChunkと、その生成根拠を同じJob結果として返す
/// </summary>
public sealed record RecognitionChunkingDiagnosticResult(
    IReadOnlyList<RecognitionChunk> Chunks,
    IReadOnlyList<RecognitionChunkDiagnosticTrace> Traces);

/// <summary>
/// 詳細診断が有効なJobでRecognitionChunkの生成根拠を取得できるChunker契約
/// </summary>
public interface IDiagnosticRecognitionChunker : IRecognitionChunker
{
    /// <summary>
    /// RecognitionChunkと分割理由を同時に生成する
    /// </summary>
    Task<RecognitionChunkingDiagnosticResult> CreateChunksWithDiagnosticsAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine内部で得た1件のASR raw結果を詳細診断用に保持する
/// </summary>
/// <remarks>
/// canonical textはCommon側で決まるためここには持たせず、Engine固有adapterが観測したraw値だけを返す。
/// TimestampTraceもEngine固有構造をJsonElementへ閉じ込め、Commonが内容を解釈しないようにする。
/// </remarks>
public sealed record AsrResultDiagnosticTrace(
    int RecognitionChunkId,
    string? RawText,
    JsonElement? TimestampTrace = null,
    bool Discarded = false,
    string? DiscardReason = null,
    long ElapsedMilliseconds = 0);

/// <summary>
/// ASR EngineがCommonへ返すEngine非依存の詳細診断traceを保持する
/// </summary>
/// <param name="RecognitionChunks">生成したRecognitionChunkと分割根拠</param>
/// <param name="ChunkGenerationMilliseconds">RecognitionChunk生成に要した時間</param>
/// <param name="AsrMilliseconds">実Recognizer呼び出しに要した時間。chunk生成時間を含まない</param>
/// <param name="AsrResults">Engine adapterが観測したraw ASR結果。診断OFFではnull</param>
public sealed record TranscriptionEngineDiagnosticTrace(
    IReadOnlyList<RecognitionChunkDiagnosticTrace> RecognitionChunks,
    long ChunkGenerationMilliseconds = 0,
    long AsrMilliseconds = 0,
    IReadOnlyList<AsrResultDiagnosticTrace>? AsrResults = null);
