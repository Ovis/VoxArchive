namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// 通常分割後に3秒未満の末尾が残る場合、直前chunkと末尾を再配分する境界を選択する
/// </summary>
/// <remarks>
/// 短い末尾をそのままASRへ渡すと文脈不足や無音padding比率の増加につながるため、
/// 直前chunkとの合計範囲を見直し、概ね等分となる位置の近傍から自然な境界を探す。
/// </remarks>
internal static class ReazonSpeechTailBoundarySelector
{
    internal const int SampleRate = 16_000;
    internal const long MaximumChunkSamples = 25L * SampleRate;
    internal const long MinimumChunkSamples = 3L * SampleRate;
    internal const long SearchRadiusSamples = 2L * SampleRate;
    internal const long MinimumSilenceSamples = 200L * SampleRate / 1000L;

    /// <summary>
    /// 直前chunk開始からSpeechRegion終端までの範囲について、短い末尾を避ける再分割境界を選択する
    /// </summary>
    /// <param name="analysis">SpeechRegion全体について一度だけ計算したRMS解析結果</param>
    /// <param name="combinedStartSample">再配分対象となる直前chunkの開始sample</param>
    /// <param name="regionEndSample">SpeechRegion終端sample</param>
    /// <returns>合計長が25秒以下で統合可能な場合はnull。再分割が必要なら境界情報を返す</returns>
    internal static ReazonSpeechTailBoundary? Select(
        ReazonSpeechRmsAnalysis analysis,
        long combinedStartSample,
        long regionEndSample)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (regionEndSample <= combinedStartSample)
        {
            throw new ArgumentOutOfRangeException(nameof(regionEndSample));
        }

        var combinedLength = regionEndSample - combinedStartSample;
        if (combinedLength <= MaximumChunkSamples)
        {
            return null;
        }

        var target = combinedStartSample + (combinedLength / 2L);
        var searchStart = Math.Max(
            combinedStartSample + MinimumChunkSamples,
            target - SearchRadiusSamples);
        var searchEnd = Math.Min(
            regionEndSample - MinimumChunkSamples,
            target + SearchRadiusSamples);
        if (searchEnd <= searchStart)
        {
            return null;
        }

        var silenceBoundary = SelectSilenceBoundary(
            analysis,
            target,
            searchStart,
            searchEnd);
        if (silenceBoundary is not null)
        {
            return silenceBoundary;
        }

        return SelectMinimumRmsBoundary(
            analysis,
            target,
            searchStart,
            searchEnd);
    }

    private static ReazonSpeechTailBoundary? SelectSilenceBoundary(
        ReazonSpeechRmsAnalysis analysis,
        long target,
        long searchStart,
        long searchEnd)
    {
        var intervals = ReazonSpeechLowVolumeIntervalBuilder.Build(
            analysis.Frames,
            analysis.P20Rms,
            searchStart,
            searchEnd);

        ReazonSpeechTailBoundary? best = null;
        foreach (var interval in intervals)
        {
            if (interval.EndSample - interval.StartSample < MinimumSilenceSamples)
            {
                continue;
            }

            var midpoint = interval.StartSample + ((interval.EndSample - interval.StartSample) / 2L);
            var candidate = new ReazonSpeechTailBoundary(
                midpoint,
                target,
                searchStart,
                searchEnd,
                UsedSilence: true,
                interval.StartSample,
                interval.EndSample,
                Rms: null);

            if (best is null
                || Math.Abs(target - midpoint) < Math.Abs(target - best.SelectedSample))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static ReazonSpeechTailBoundary? SelectMinimumRmsBoundary(
        ReazonSpeechRmsAnalysis analysis,
        long target,
        long searchStart,
        long searchEnd)
    {
        ReazonSpeechTailBoundary? best = null;
        double? bestRms = null;
        foreach (var frame in analysis.Frames)
        {
            var midpoint = frame.StartSample + ((frame.EndSample - frame.StartSample) / 2L);
            if (midpoint < searchStart || midpoint > searchEnd)
            {
                continue;
            }

            if (best is null
                || frame.Rms < bestRms!.Value
                || (frame.Rms.Equals(bestRms.Value)
                    && Math.Abs(target - midpoint) < Math.Abs(target - best.SelectedSample)))
            {
                best = new ReazonSpeechTailBoundary(
                    midpoint,
                    target,
                    searchStart,
                    searchEnd,
                    UsedSilence: false,
                    SilenceStartSample: null,
                    SilenceEndSample: null,
                    frame.Rms);
                bestRms = frame.Rms;
            }
        }

        return best;
    }
}

/// <summary>短い末尾を再配分するために採用した境界と判定根拠を保持する</summary>
internal sealed record ReazonSpeechTailBoundary(
    long SelectedSample,
    long TargetSample,
    long SearchStartSample,
    long SearchEndSample,
    bool UsedSilence,
    long? SilenceStartSample,
    long? SilenceEndSample,
    double? Rms);
