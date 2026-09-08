namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech K2へ渡すRecognitionChunkへ固定の前後無音を付加する
/// </summary>
/// <remarks>
/// ReazonSpeech K2 v2の公式利用方法に合わせて前後0.9秒の無音を付加する。
/// この無音はVADのpaddingとは別物であり、RecognitionChunkの原音上の開始・終了位置は変更しない。
/// </remarks>
internal static class ReazonSpeechK2InputPadding
{
    internal const int SampleRate = 16_000;
    internal const int PaddingMilliseconds = 900;
    internal const int PaddingSamples = PaddingMilliseconds * SampleRate / 1000;

    /// <summary>
    /// RecognitionChunkから切り出した実音声へ前後0.9秒の無音を追加する
    /// </summary>
    /// <param name="samples">RecognitionChunkの実音声sample</param>
    /// <returns>前後0.9秒の無音を含むK2入力sample</returns>
    internal static float[] Apply(ReadOnlySpan<float> samples)
    {
        var result = new float[checked(samples.Length + (PaddingSamples * 2))];
        samples.CopyTo(result.AsSpan(PaddingSamples, samples.Length));
        return result;
    }
}
