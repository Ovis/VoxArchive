using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Sileroが返したpadding前のraw regionをVoxArchive共通SpeechRegionへ変換する
/// </summary>
internal static class SileroVadRegionBuilder
{
    /// <summary>
    /// raw regionへ前後paddingを適用し、接触・重複した区間をmergeする
    /// </summary>
    /// <param name="rawRegions">Sileroが直接返した発話区間</param>
    /// <param name="sampleCount">Prepared Audioの有効sample数</param>
    /// <param name="sampleRate">Prepared Audioのsample rate</param>
    /// <param name="options">ジョブ開始時に固定されたSilero設定</param>
    internal static IReadOnlyList<SpeechRegion> Build(
        IReadOnlyList<AudioSampleRange> rawRegions,
        long sampleCount,
        int sampleRate,
        SileroVadOptions options)
    {
        ArgumentNullException.ThrowIfNull(rawRegions);
        ArgumentNullException.ThrowIfNull(options);
        if (sampleCount <= 0 || rawRegions.Count == 0) return [];
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var prePadding = ToSamples(options.PrePaddingMilliseconds, sampleRate);
        var postPadding = ToSamples(options.PostPaddingMilliseconds, sampleRate);
        var padded = new List<RegionCandidate>(rawRegions.Count);

        for (var rawId = 0; rawId < rawRegions.Count; rawId++)
        {
            var raw = rawRegions[rawId];
            var coreStart = Math.Clamp(raw.StartSample, 0L, sampleCount);
            var coreEnd = Math.Clamp(raw.EndSample, 0L, sampleCount);
            if (coreEnd <= coreStart) continue;

            padded.Add(new RegionCandidate(
                Math.Max(0L, coreStart - prePadding),
                Math.Min(sampleCount, coreEnd + postPadding),
                [new AudioSampleRange(coreStart, coreEnd)],
                [rawId]));
        }
        if (padded.Count == 0) return [];

        var merged = new List<RegionCandidate> { padded[0] };
        for (var i = 1; i < padded.Count; i++)
        {
            var current = padded[i];
            var previous = merged[^1];

            // SileroではMinimum Silence Durationが発話分離を担うため、独自のMerge Gapは設けない。
            // padding後に実際に接触または重複した場合だけ同一SpeechRegionへ統合する。
            if (current.StartSample <= previous.EndSample)
            {
                merged[^1] = new RegionCandidate(
                    previous.StartSample,
                    Math.Max(previous.EndSample, current.EndSample),
                    [.. previous.CoreRanges, .. current.CoreRanges],
                    [.. previous.SourceRawSpeechRegionIds, .. current.SourceRawSpeechRegionIds]);
            }
            else
            {
                merged.Add(current);
            }
        }

        var result = new SpeechRegion[merged.Count];
        for (var i = 0; i < merged.Count; i++)
        {
            var region = merged[i];
            result[i] = new SpeechRegion(
                i,
                region.StartSample,
                region.EndSample,
                region.CoreRanges,
                region.SourceRawSpeechRegionIds);
        }
        return result;
    }

    private static long ToSamples(int milliseconds, int sampleRate)
        => Math.Max(0L, (long)Math.Ceiling(milliseconds / 1000d * sampleRate));

    private sealed record RegionCandidate(
        long StartSample,
        long EndSample,
        IReadOnlyList<AudioSampleRange> CoreRanges,
        IReadOnlyList<int> SourceRawSpeechRegionIds);
}
