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
/// ASR EngineがCommonへ返すEngine非依存の詳細診断traceを保持する
/// </summary>
public sealed record TranscriptionEngineDiagnosticTrace(
    IReadOnlyList<RecognitionChunkDiagnosticTrace> RecognitionChunks);
