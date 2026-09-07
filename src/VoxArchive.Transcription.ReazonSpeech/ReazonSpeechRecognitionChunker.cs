using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech向けにSpeechRegionをASR呼び出し単位へ変換する
/// </summary>
/// <remarks>
/// この段階では既存動作を維持するため1:1変換だけを行う。
/// 後続のK2 25秒制約対応をこのクラスへ閉じ込め、VAD結果であるSpeechRegionを変更しない。
/// </remarks>
public sealed class ReazonSpeechRecognitionChunker : IRecognitionChunker
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
