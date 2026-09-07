namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2向けRecognitionChunkの通常分割に使用する連続低音量区間から境界候補を選択する
/// </summary>
/// <remarks>
/// RMS閾値自体はSpeechRegion.CoreRangesから求める一方、境界候補はpaddingや発話間gapを含む
/// SpeechRegion全体から探索する。これによりVADが保持した原音タイムラインを壊さず、自然な無音位置で分割できる。
/// </remarks>
internal static class ReazonSpeechSilenceBoundarySelector
{
    internal const int SampleRate = 16_000;
    internal const long MinimumSearchOffsetSamples = 15L * SampleRate;
    internal const long TargetChunkSamples = 25L * SampleRate;
    internal const long MinimumSilenceSamples = 200L * SampleRate / 1000L;
    internal const long MinimumChunkSamples = 3L * SampleRate;

    /// <summary>
    /// 指定chunk開始位置から15～25秒の範囲で、25秒地点に最も近い有効な無音中央を返す
    /// </summary>
    /// <param name="analysis">SpeechRegion全体について一度だけ計算したRMS解析結果</param>
    /// <param name="chunkStartSample">現在生成中のchunk開始sample</param>
    /// <param name="regionEndSample">元SpeechRegionの終端sample</param>
    /// <returns>有効な通常無音候補がない場合はnull</returns>
    internal static ReazonSpeechSilenceBoundary? Select(
        ReazonSpeechRmsAnalysis analysis,
        long chunkStartSample,
        long regionEndSample)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (regionEndSample <= chunkStartSample)
        {
            throw new ArgumentOutOfRangeException(nameof(regionEndSample));
        }

        var searchStart = chunkStartSample + MinimumSearchOffsetSamples;
        var target = chunkStartSample + TargetChunkSamples;
        var searchEnd = Math.Min(target, regionEndSample);
        if (searchEnd <= searchStart)
        {
            return null;
        }

        var lowVolumeIntervals = BuildContinuousLowVolumeIntervals(
            analysis.Frames,
            analysis.P20Rms,
            searchStart,
            searchEnd);

        ReazonSpeechSilenceBoundary? best = null;
        foreach (var interval in lowVolumeIntervals)
        {
            if (interval.EndSample - interval.StartSample < MinimumSilenceSamples)
            {
                continue;
            }

            var midpoint = interval.StartSample + ((interval.EndSample - interval.StartSample) / 2L);
            if (midpoint - chunkStartSample < MinimumChunkSamples
                || regionEndSample - midpoint < MinimumChunkSamples)
            {
                continue;
            }

            var candidate = new ReazonSpeechSilenceBoundary(
                midpoint,
                interval.StartSample,
                interval.EndSample,
                target,
                searchStart,
                searchEnd);

            // 無音長は200msを満たした後の優先順位へ使わない。
            // 目標25秒に最も近い中央だけを比較し、同距離なら先に現れた候補を維持して結果を安定させる。
            if (best is null
                || Math.Abs(target - candidate.SelectedSample) < Math.Abs(target - best.SelectedSample))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static IReadOnlyList<ReazonSpeechLowVolumeInterval> BuildContinuousLowVolumeIntervals(
        IReadOnlyList<ReazonSpeechRmsFrame> frames,
        double threshold,
        long searchStart,
        long searchEnd)
    {
        var result = new List<ReazonSpeechLowVolumeInterval>();
        ReazonSpeechLowVolumeInterval? current = null;

        foreach (var frame in frames)
        {
            if (frame.Rms > threshold)
            {
                continue;
            }

            // 30ms windowが探索範囲からはみ出していても、探索範囲内に実際に重なる低音量sampleだけを候補へ含める。
            // 連続性は低音量frameが覆うsample区間の和集合で判定するため、10ms hopで重なるframeは一つの区間へ統合する。
            var clippedStart = Math.Max(frame.StartSample, searchStart);
            var clippedEnd = Math.Min(frame.EndSample, searchEnd);
            if (clippedEnd <= clippedStart)
            {
                continue;
            }

            if (current is null)
            {
                current = new ReazonSpeechLowVolumeInterval(clippedStart, clippedEnd);
                continue;
            }

            if (clippedStart <= current.EndSample)
            {
                current = current with { EndSample = Math.Max(current.EndSample, clippedEnd) };
                continue;
            }

            result.Add(current);
            current = new ReazonSpeechLowVolumeInterval(clippedStart, clippedEnd);
        }

        if (current is not null)
        {
            result.Add(current);
        }
        return result;
    }
}

/// <summary>通常無音分割で採用した境界と診断に必要な探索情報を保持する</summary>
internal sealed record ReazonSpeechSilenceBoundary(
    long SelectedSample,
    long SilenceStartSample,
    long SilenceEndSample,
    long TargetSample,
    long SearchStartSample,
    long SearchEndSample);

/// <summary>低音量RMS frameのsample区間を和集合化した連続区間を表す</summary>
internal sealed record ReazonSpeechLowVolumeInterval(long StartSample, long EndSample);
