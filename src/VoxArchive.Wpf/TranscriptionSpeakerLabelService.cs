using NAudio.Wave;

namespace VoxArchive.Wpf;

/// <summary>
/// 録音元の左右チャンネルのエネルギーから文字起こし区間の話者を判定する
/// </summary>
internal sealed class TranscriptionSpeakerLabelService
{
    /// <summary>
    /// 各文字起こし区間へ既存仕様と同じSpeaker/Mic/Mixedラベルを付与する
    /// </summary>
    public IReadOnlyList<TranscribedSegment> Apply(string audioFilePath, IReadOnlyList<TranscribedSegment> segments, CancellationToken cancellationToken)
    {
        if (segments.Count == 0) return segments;

        try
        {
            using var reader = new AudioFileReader(audioFilePath);
            if (reader.WaveFormat.Channels < 2) return segments;

            var sampleRate = reader.WaveFormat.SampleRate;
            var channels = reader.WaveFormat.Channels;
            var ranges = BuildSegmentFrameRanges(segments, sampleRate);
            var leftEnergy = new double[segments.Count];
            var rightEnergy = new double[segments.Count];
            var buffer = new float[Math.Max(4096, sampleRate / 4) * channels];
            var segmentIndex = 0;
            long frameIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                var frames = read / channels;
                for (var frame = 0; frame < frames; frame++, frameIndex++)
                {
                    while (segmentIndex < ranges.Count && frameIndex >= ranges[segmentIndex].EndFrame) segmentIndex++;
                    if (segmentIndex >= ranges.Count) break;
                    var range = ranges[segmentIndex];
                    if (frameIndex < range.StartFrame) continue;

                    var sampleIndex = frame * channels;
                    var left = buffer[sampleIndex];
                    var right = buffer[sampleIndex + 1];
                    leftEnergy[segmentIndex] += left * left;
                    rightEnergy[segmentIndex] += right * right;
                }
                if (segmentIndex >= ranges.Count) break;
            }

            var labeled = new List<TranscribedSegment>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                labeled.Add(segments[i] with { SpeakerLabel = ResolveSpeakerLabel(leftEnergy[i], rightEnergy[i]) });
            }
            return labeled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 既存仕様では話者判定だけの失敗で文字起こし全体を失敗させないため、その挙動を維持する。
            return segments;
        }
    }

    private static IReadOnlyList<SegmentFrameRange> BuildSegmentFrameRanges(IReadOnlyList<TranscribedSegment> segments, int sampleRate)
    {
        var ranges = new List<SegmentFrameRange>(segments.Count);
        foreach (var segment in segments)
        {
            var startSeconds = Math.Max(0d, segment.Start.TotalSeconds);
            var endSeconds = Math.Max(startSeconds + 0.02d, segment.End.TotalSeconds);
            var startFrame = (long)Math.Floor(startSeconds * sampleRate);
            var endFrame = (long)Math.Ceiling(endSeconds * sampleRate);
            if (endFrame <= startFrame) endFrame = startFrame + 1;
            ranges.Add(new SegmentFrameRange(startFrame, endFrame));
        }
        return ranges;
    }

    private static string ResolveSpeakerLabel(double leftEnergy, double rightEnergy)
    {
        const double epsilon = 1e-10;
        const double sameLevelThresholdDb = 2.5;
        var diffDb = 10d * Math.Log10((rightEnergy + epsilon) / (leftEnergy + epsilon));
        if (Math.Abs(diffDb) < sameLevelThresholdDb) return "Mixed";
        return diffDb > 0 ? "Mic" : "Speaker";
    }

    private sealed record SegmentFrameRange(long StartFrame, long EndFrame);
}
