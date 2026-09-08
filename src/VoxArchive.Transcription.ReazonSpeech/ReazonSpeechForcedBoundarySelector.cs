namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// 通常の200ms無音候補が見つからない場合にK2 RecognitionChunkの強制分割境界を選択する
/// </summary>
/// <remarks>
/// 発話中を機械的に25秒位置で切るのではなく、25秒直前1秒の30ms RMS系列から最も音量の低い位置を選ぶ。
/// これにより無音が存在しない長い発話でも、音声エネルギーが比較的小さい位置へ境界を寄せる。
/// </remarks>
internal static class ReazonSpeechForcedBoundarySelector
{
    internal const int SampleRate = 16_000;
    internal const long TargetChunkSamples = 25L * SampleRate;
    internal const long SearchDurationSamples = 1L * SampleRate;
    internal const long MinimumChunkSamples = 3L * SampleRate;

    /// <summary>
    /// 25秒直前1秒のRMS frameから最小RMSの中央sampleを強制分割境界として選択する
    /// </summary>
    /// <param name="analysis">SpeechRegion全体について一度だけ計算したRMS解析結果</param>
    /// <param name="chunkStartSample">現在生成中のchunk開始sample</param>
    /// <param name="regionEndSample">元SpeechRegionの終端sample</param>
    /// <returns>左右3秒以上を確保できる候補がない場合はnull</returns>
    internal static ReazonSpeechForcedBoundary? Select(
        ReazonSpeechRmsAnalysis analysis,
        long chunkStartSample,
        long regionEndSample)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (regionEndSample <= chunkStartSample)
        {
            throw new ArgumentOutOfRangeException(nameof(regionEndSample));
        }

        var target = chunkStartSample + TargetChunkSamples;
        var searchStart = target - SearchDurationSamples;
        var searchEnd = Math.Min(target, regionEndSample);
        if (searchEnd <= searchStart)
        {
            return null;
        }

        ReazonSpeechForcedBoundary? best = null;
        double? bestRms = null;
        foreach (var frame in analysis.Frames)
        {
            var midpoint = frame.StartSample + ((frame.EndSample - frame.StartSample) / 2L);
            if (midpoint < searchStart || midpoint > searchEnd)
            {
                continue;
            }
            if (midpoint - chunkStartSample < MinimumChunkSamples
                || regionEndSample - midpoint < MinimumChunkSamples)
            {
                continue;
            }

            var candidate = new ReazonSpeechForcedBoundary(
                midpoint,
                frame.StartSample,
                frame.EndSample,
                frame.Rms,
                target,
                searchStart,
                searchEnd);

            if (best is null
                || frame.Rms < bestRms!.Value
                || (frame.Rms.Equals(bestRms.Value) && midpoint > best.SelectedSample))
            {
                // RMSが同値の場合は25秒側に近いframeを採用する。
                // 同品質なら長いchunkを優先した方が不要な追加分割を増やさず、結果も決定的になる。
                best = candidate;
                bestRms = frame.Rms;
            }
        }

        return best;
    }
}

/// <summary>forced splitで採用したRMS frameと探索条件を診断可能な形で保持する</summary>
internal sealed record ReazonSpeechForcedBoundary(
    long SelectedSample,
    long FrameStartSample,
    long FrameEndSample,
    double Rms,
    long TargetSample,
    long SearchStartSample,
    long SearchEndSample);
