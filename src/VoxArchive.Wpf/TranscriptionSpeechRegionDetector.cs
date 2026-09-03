using NAudio.Wave;

namespace VoxArchive.Wpf;

/// <summary>
/// 正規化済み音声から文字起こし対象となる発話区間を検出する
/// </summary>
internal sealed class TranscriptionSpeechRegionDetector
{
    private const double FrameMilliseconds = 30d;
    private const double MinSpeechMilliseconds = 250d;
    private const double MinSilenceMilliseconds = 600d;
    private const double SpeechPaddingMilliseconds = 200d;
    private const double MergeGapMilliseconds = 300d;
    private const int AnalysisFrameCapacity = 4096;
    private const double NoiseFloorPercentile = 0.2d;
    private const double MinimumThresholdDb = -50d;
    private const double ThresholdOffsetDb = 12d;

    /// <summary>
    /// 指定したモノラルWAVEから既存VADアルゴリズムと同じ条件で発話区間を検出する
    /// </summary>
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(string monoWavePath, CancellationToken cancellationToken)
    {
        return await Task.Run(() => Detect(monoWavePath, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<SpeechRegion> Detect(string monoWavePath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(monoWavePath);
        if (reader.WaveFormat.Channels != 1)
        {
            return [new SpeechRegion(TimeSpan.Zero, reader.TotalTime)];
        }

        var sampleRate = reader.WaveFormat.SampleRate;
        var frameSamples = Math.Max(1, (int)Math.Round(sampleRate * (FrameMilliseconds / 1000d)));
        var minSpeechFrames = Math.Max(1, (int)Math.Ceiling(MinSpeechMilliseconds / FrameMilliseconds));
        var minSilenceFrames = Math.Max(1, (int)Math.Ceiling(MinSilenceMilliseconds / FrameMilliseconds));
        var paddingFrames = Math.Max(0, (int)Math.Ceiling(SpeechPaddingMilliseconds / FrameMilliseconds));
        var mergeGapFrames = Math.Max(0, (int)Math.Ceiling(MergeGapMilliseconds / FrameMilliseconds));
        var dbFrames = new List<double>(AnalysisFrameCapacity);
        var buffer = new float[frameSamples];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, frameSamples);
            if (read <= 0) break;

            var sum = 0d;
            for (var i = 0; i < read; i++) sum += buffer[i] * buffer[i];
            dbFrames.Add(ToDecibel(Math.Sqrt(sum / Math.Max(1, read))));
        }

        if (dbFrames.Count == 0) return Array.Empty<SpeechRegion>();

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
        if (ranges.Count == 0) return Array.Empty<SpeechRegion>();

        for (var i = 0; i < ranges.Count; i++)
        {
            ranges[i] = (Math.Max(0, ranges[i].StartFrame - paddingFrames), Math.Min(dbFrames.Count - 1, ranges[i].EndFrame + paddingFrames));
        }

        var merged = new List<(int StartFrame, int EndFrame)> { ranges[0] };
        for (var i = 1; i < ranges.Count; i++)
        {
            var current = ranges[i];
            var last = merged[^1];
            if (current.StartFrame - last.EndFrame <= mergeGapFrames)
            {
                merged[^1] = (last.StartFrame, Math.Max(last.EndFrame, current.EndFrame));
            }
            else
            {
                merged.Add(current);
            }
        }

        var result = new List<SpeechRegion>(merged.Count);
        foreach (var range in merged)
        {
            var start = TimeSpan.FromSeconds(range.StartFrame * FrameMilliseconds / 1000d);
            var end = TimeSpan.FromSeconds((range.EndFrame + 1) * FrameMilliseconds / 1000d);
            if (end > reader.TotalTime) end = reader.TotalTime;
            if (end > start) result.Add(new SpeechRegion(start, end));
        }
        return result;
    }

    private static void AddSpeechRangeIfValid(ICollection<(int StartFrame, int EndFrame)> ranges, int start, int end, int minFrames)
    {
        if (end - start + 1 >= minFrames) ranges.Add((start, end));
    }

    private static double ToDecibel(double rms) => 20d * Math.Log10(Math.Max(rms, 1e-9d));

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return -120d;
        var ordered = values.OrderBy(x => x).ToArray();
        var index = (int)Math.Floor((ordered.Length - 1) * Math.Clamp(percentile, 0d, 1d));
        return ordered[index];
    }
}
