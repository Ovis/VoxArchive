using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2がpadding込み入力に対して返した時刻をRecognitionChunk基準へ補正する
/// </summary>
/// <remarks>
/// K2入力の先頭へ0.9秒の人工無音を追加するため、raw timestampから0.9秒を引いた後に
/// RecognitionChunkの実音声範囲へclampする。補正後に長さを失った結果はcanonical結果へ採用しない。
/// </remarks>
internal static class ReazonSpeechK2TimestampNormalizer
{
    private const double PaddingSeconds = ReazonSpeechK2InputPadding.PaddingMilliseconds / 1000d;

    /// <summary>
    /// K2 raw timestampをchunk-relative sample座標へ変換する
    /// </summary>
    /// <param name="rawStartSeconds">padding込みK2入力上の開始秒</param>
    /// <param name="rawEndSeconds">padding込みK2入力上の終了秒</param>
    /// <param name="chunkLengthSamples">RecognitionChunk実音声のsample数</param>
    /// <returns>補正後に正の長さが残る場合のみchunk-relative sample範囲</returns>
    internal static AudioSampleRange? Normalize(
        double rawStartSeconds,
        double rawEndSeconds,
        long chunkLengthSamples)
    {
        if (!double.IsFinite(rawStartSeconds) || !double.IsFinite(rawEndSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(rawStartSeconds), "K2 timestampは有限値である必要があります。");
        }
        if (chunkLengthSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkLengthSamples));
        }

        var correctedStart = rawStartSeconds - PaddingSeconds;
        var correctedEnd = rawEndSeconds - PaddingSeconds;

        var startSample = SecondsToStartSample(correctedStart);
        var endSample = SecondsToEndSample(correctedEnd);
        startSample = Math.Clamp(startSample, 0L, chunkLengthSamples);
        endSample = Math.Clamp(endSample, 0L, chunkLengthSamples);

        if (endSample <= startSample)
        {
            return null;
        }
        return new AudioSampleRange(startSample, endSample);
    }

    private static long SecondsToStartSample(double seconds)
        => checked((long)Math.Floor(seconds * ReazonSpeechK2InputPadding.SampleRate));

    private static long SecondsToEndSample(double seconds)
        => checked((long)Math.Ceiling(seconds * ReazonSpeechK2InputPadding.SampleRate));
}
