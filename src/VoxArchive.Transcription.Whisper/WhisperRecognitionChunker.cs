using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper向けにSpeechRegionをASR呼び出し単位へ変換する
/// </summary>
/// <remarks>
/// 現段階ではWhisper固有の最大長分割を行わず、SpeechRegion 1件をRecognitionChunk 1件へそのまま対応させる。
/// 将来Whisper側へ独自chunkingを導入してもVADの責務を変更せずに済むよう、明示的なChunkerとして分離する。
/// </remarks>
public sealed class WhisperRecognitionChunker : IRecognitionChunker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RecognitionChunk>> CreateChunksAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speechRegions);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RecognitionChunk> chunks = speechRegions
            .Select((region, index) => new RecognitionChunk(
                index,
                region.SpeechRegionId,
                region.StartSample,
                region.EndSample))
            .ToArray();
        return Task.FromResult(chunks);
    }
}
