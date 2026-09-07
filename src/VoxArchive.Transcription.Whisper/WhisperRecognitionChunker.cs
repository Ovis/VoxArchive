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
    public IReadOnlyList<RecognitionChunk> CreateChunks(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speechRegions);

        var chunks = new List<RecognitionChunk>(speechRegions.Count);
        for (var i = 0; i < speechRegions.Count; i++)
        {
            var region = speechRegions[i];
            chunks.Add(new RecognitionChunk(
                i,
                region.SpeechRegionId,
                region.StartSample,
                region.EndSample));
        }
        return chunks;
    }
}
