using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech向けにSpeechRegionをASR呼び出し単位へ変換する
/// </summary>
/// <remarks>
/// この段階では既存動作を維持するため1:1変換だけを行う。
/// 後続のK2 25秒制約対応ではPrepared AudioをRMS解析するため、非同期Chunker契約へ先行移行している。
/// </remarks>
public sealed class ReazonSpeechRecognitionChunker : IRecognitionChunker
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
