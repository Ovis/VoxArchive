namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2がpadding込み入力に対して返したsubword timestampをRecognitionChunk基準へ補正する
/// </summary>
/// <remarks>
/// ReazonSpeech K2 v2の公式APIが返すtimestampはsubwordごとの単一点であり、開始・終了rangeではない。
/// そのため存在しない終了時刻を推測せず、raw point timestampだけをsample座標へ変換して0.9秒分を補正する。
/// 秒同士を先に減算すると浮動小数点誤差で境界sampleが1つずれる場合があるため、
/// canonical timelineであるsample座標へ変換してから整数sampleのpaddingを引く。
/// </remarks>
internal static class ReazonSpeechK2TimestampNormalizer
{
    /// <summary>
    /// K2 raw point timestampをchunk-relative sample座標へ変換する
    /// </summary>
    /// <param name="rawSeconds">padding込みK2入力上のsubword timestamp秒</param>
    /// <param name="chunkLengthSamples">RecognitionChunk実音声のsample数</param>
    /// <returns>0からchunkLengthSamplesへclampしたchunk-relative sample位置</returns>
    internal static long NormalizePoint(double rawSeconds, long chunkLengthSamples)
    {
        if (!double.IsFinite(rawSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(rawSeconds), "K2 timestampは有限値である必要があります。");
        }
        if (chunkLengthSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkLengthSamples));
        }

        // point timestampは開始位置と同様にfloorでsampleへ寄せる。
        // raw秒をsample化してから14,400sampleを引くことで、1.4 - 0.9のdouble減算誤差を避ける。
        var rawSample = checked((long)Math.Floor(rawSeconds * ReazonSpeechK2InputPadding.SampleRate));
        var correctedSample = rawSample - ReazonSpeechK2InputPadding.PaddingSamples;
        return Math.Clamp(correctedSample, 0L, chunkLengthSamples);
    }
}
