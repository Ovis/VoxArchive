namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2がpadding込み入力に対して返したsubword timestampをRecognitionChunk基準へ補正する
/// </summary>
/// <remarks>
/// ReazonSpeech K2 v2の公式APIが返すtimestampはsubwordごとの単一点であり、開始・終了rangeではない。
/// そのため存在しない終了時刻を推測せず、raw point timestampだけをsample座標へ変換して0.9秒分を補正する。
/// sherpa-onnxはtimestampをfloatで返すため、公称1.4秒のような値も内部的にはわずかに小さくなる場合がある。
/// point timestampをfloorするとその量子化誤差を1sample前倒ししてしまうため、最寄りsampleへ丸めてから整数sampleのpaddingを引く。
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

        // point timestampは区間境界ではないため、包含関係を守るfloor/ceilではなく最寄りsampleへ寄せる。
        // raw秒をsample化してから14,400sampleを引くことで、padding補正自体も整数座標上で完結させる。
        var rawSample = checked((long)Math.Round(
            rawSeconds * ReazonSpeechK2InputPadding.SampleRate,
            MidpointRounding.AwayFromZero));
        var correctedSample = rawSample - ReazonSpeechK2InputPadding.PaddingSamples;
        return Math.Clamp(correctedSample, 0L, chunkLengthSamples);
    }
}
