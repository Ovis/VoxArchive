using NAudio.Wave;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// 前処理済み音声から既存VAD規則で発話区間を検出する
/// </summary>
public sealed class TranscriptionSpeechRegionDetector : ISpeechRegionDetector
{
    private const double FrameMilliseconds = 30d;
    private const double MinSpeechMilliseconds = 250d;
    private const double MinSilenceMilliseconds = 600d;
    private const double SpeechPaddingMilliseconds = 200d;
    private const double MergeGapMilliseconds = 300d;
    private const int AnalysisFrameCapacity = 4096;
    private const double NoiseFloorPercentile = 0.2d;
    private const double MinimumThresholdDb = -50dB;
    private const double ThresholdOffsetDb = 12d;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);

        // この実装はSileroが利用できない場合の互換fallbackであり、Silero固有設定は意図的に適用しない。
        // 既存の音量ベースVAD条件を変えるとfallback時だけ従来結果が変化するため、内部定数を維持する。
        await using var stream = await audio.OpenReadAsync(cancellationToken);
        var detected = await Task.Run(() => Detect(stream, cancellationToken), cancellationToken);

        // Durationから再計算するとresamplingの末尾丸めで1sampleずれる可能性があるため、
        // Prepared Audioが保持する実sample数で最終範囲をclampする。
        return ClampToSampleCount(detected, audio.SampleCount);
    }

    private static IReadOnlyList<SpeechRegion> Detect(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(stream);
        ISampleProvider sampleProvider = reader.ToSampleProvider();
        var sampleRate = sampleProvider.WaveFormat.SampleRate;
        var totalSamples = Math.Max(0L, reader.WaveFormat.BlockAlign > 0
            ? reader.Length / reader.WaveFormat.BlockAlign
            : 0L);
        if (sampleProvider.WaveFormat.Channels != 1)
        {
            return totalSamples > 0
                ? [CreateRegion(0, 0, totalSamples, [new AudioSampleRange(0, totalSamples)], [0])]
                : [];
        }

        var frameSamples = Math.Max(1, (int)Math.Round(sampleRate * (FrameMilliseconds / 1000d)));
        var minSpeechFrames = Math.Max(1, (int)Math.Ceiling(MinSpeechMilliseconds / FrameMilliseconds));
        var minSilenceFrames = Math.Max(1, (int)Math.Ceiling(MinSilenceMilliseconds / FrameMilliseconds));
        var paddingSamples = Math.Max(0L, (long)Math.Ceiling(sampleRate * SpeechPaddingMilliseconds / 1000d));
        var mergeGapSamples = Math.Max(0L, (long)Math.Ceiling(sampleRate * MergeGapMilliseconds / 1000d));
        var dbFrames = new List<double>(AnalysisFrameCapacity);
        var frameLengths = new List<int>(AnalysisFrameCapacity);
        var buffer = new float[frameSamples];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = sampleProvider.Read(buffer.AsSpan());
            if (read <= 0)
            {
                break;
            }

            var sum = 0d;
            for (var i = 0; i < read; i++) sum += buffer[i] * buffer[i];
            dbFrames.Add(ToDecibel(Math.Sqrt(sum / Math.Max(1, read))));
            frameLengths.Add(read);
        }

        if (dbFrames.Count == 0) return [];

        var noiseFloorDb = Percentile(dbFrames, NoiseFloorPercentile);
        var speechThresholdDb = Math.Max(MinimumThresholdDb, noiseFloorDb + ThresholdOffsetDb);
        var ranges = new List<(int StartFrame, int EndFrame)>();
        var inSpeech = false;
        var speechStart = 0;
        var trailingSilence = 0;

        for (var i = 0; i < dbFrames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSpeechFrame = dbFrames[i] >= speechThresholdDb;
            if (!inSpeech)
            {
                if (isSpeechFrame)
                {
                    inSpeech = true;
                    speechStart = i;
                    trailingSilence = 0;
                }
                continue;
            }

            if (isSpeechFrame)
            {
                trailingSilence = 0;
                continue;
            }

            trailingSilence++;
            if (trailingSilence < minSilenceFrames) continue;
            AddSpeechRangeIfValid(ranges, speechStart, i - trailingSilence, minSpeechFrames);
            inSpeech = false;
            trailingSilence = 0;
        }

        if (inSpeech) AddSpeechRangeIfValid(ranges, speechStart, dbFrames.Count - 1, minSpeechFrames);
        if (ranges.Count == 0) return [];

        var frameStarts = new long[frameLengths.Count + 1];
        for (var i = 0; i < frameLengths.Count; i++) frameStarts[i + 1] = frameStarts[i] + frameLengths[i];

        var padded = new List<DetectedRange>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            var coreStart = frameStarts[range.StartFrame];
            var coreEnd = frameStarts[Math.Min(range.EndFrame + 1, frameStarts.Length - 1)];
            padded.Add(new DetectedRange(
                Math.Max(0, coreStart - paddingSamples),
                Math.Min(totalSamples, coreEnd + paddingSamples),
                [new AudioSampleRange(coreStart, coreEnd)],
                [i]));
        }

        var merged = new List<DetectedRange> { padded[0] };
        for (var i = 1; i < padded.Count; i++)
        {
            var current = padded[i];
            var last = merged[^1];
            if (current.StartSample - last.EndSample <= mergeGapSamples)
            {
                merged[^1] = new DetectedRange(
                    last.StartSample,
                    Math.Max(last.EndSample, current.EndSample),
                    [.. last.CoreRanges, .. current.CoreRanges],
                    [.. last.SourceRawSpeechRegionIds, .. current.SourceRawSpeechRegionIds]);
            }
            else
            {
                merged.Add(current);
            }
        }

        var result = new List<SpeechRegion>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var range = merged[i];
            if (range.EndSample > range.StartSample)
            {
                result.Add(CreateRegion(i, range.StartSample, range.EndSample, range.CoreRanges, range.SourceRawSpeechRegionIds));
            }
        }
        return result;
    }

    private static IReadOnlyList<SpeechRegion> ClampToSampleCount(IReadOnlyList<SpeechRegion> regions, long sampleCount)
    {
        if (sampleCount <= 0 || regions.Count == 0) return [];
        var result = new List<SpeechRegion>(regions.Count);
        foreach (var region in regions)
        {
            var start = Math.Clamp(region.StartSample, 0L, sampleCount);
            var end = Math.Clamp(region.EndSample, 0L, sampleCount);
            if (end <= start) continue;
            var cores = region.CoreRanges
                .Select(x => new AudioSampleRange(Math.Clamp(x.StartSample, start, end), Math.Clamp(x.EndSample, start, end)))
                .Where(x => x.EndSample > x.StartSample)
                .ToArray();
            result.Add(region with { StartSample = start, EndSample = end, CoreRanges = cores });
        }
        return result;
    }

    private static SpeechRegion CreateRegion(int id, long startSample, long endSample, IReadOnlyList<AudioSampleRange> coreRanges, IReadOnlyList<int> sourceRawSpeechRegionIds)
        => new(id, startSample, endSample, coreRanges, sourceRawSpeechRegionIds);

    private static void AddSpeechRangeIfValid(ICollection<(int StartFrame, int EndFrame)> ranges, int start, int end, int minFrames)
    {
        if (end - start + 1 >= minFrames) ranges.Add((start, end));
    }

    private static double ToDecibel(double rms) => 20d * Math.Log10(Math.Max(rms, 1e-9d));

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        var index = (int)Math.Floor((ordered.Length - 1) * Math.Clamp(percentile, 0d, 1d));
        return ordered[index];
    }

    private sealed record DetectedRange(long StartSample, long EndSample, IReadOnlyList<AudioSampleRange> CoreRanges, IReadOnlyList<int> SourceRawSpeechRegionIds);
}
