using NAudio.Wave;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// original recordingのCH1/CH2エネルギーから認識segmentへ話者ラベルを付与する
/// </summary>
public sealed class TranscriptionSpeakerLabelService
{
    /// <summary>
    /// 認識結果へSpeaker/Mic/Mixedラベルを付与する
    /// </summary>
    /// <param name="audioFilePath">前処理前の元録音ファイル</param>
    /// <param name="segments">Engineが返したabsolute timelineのsegment</param>
    /// <param name="cancellationToken">処理のキャンセルを通知するトークン</param>
    public IReadOnlyList<LabeledTranscriptionSegment> Apply(
        string audioFilePath,
        IReadOnlyList<RecognizedTranscriptionSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return Array.Empty<LabeledTranscriptionSegment>();
        }

        try
        {
            using var reader = new AudioFileReader(audioFilePath);
            if (reader.WaveFormat.Channels < 2)
            {
                return segments.Select(x => new LabeledTranscriptionSegment(x, null)).ToArray();
            }

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
                if (read <= 0)
                {
                    break;
                }

                var frames = read / channels;
                for (var frame = 0; frame < frames; frame++, frameIndex++)
                {
                    while (segmentIndex < ranges.Count && frameIndex >= ranges[segmentIndex].EndFrame)
                    {
                        segmentIndex++;
                    }
                    if (segmentIndex >= ranges.Count)
                    {
                        break;
                    }

                    var range = ranges[segmentIndex];
                    if (frameIndex < range.StartFrame)
                    {
                        continue;
                    }

                    var sampleIndex = frame * channels;
                    var left = buffer[sampleIndex];
                    var right = buffer[sampleIndex + 1];
                    leftEnergy[segmentIndex] += left * left;
                    rightEnergy[segmentIndex] += right * right;
                }

                if (segmentIndex >= ranges.Count)
                {
                    break;
                }
            }

            var labeled = new List<LabeledTranscriptionSegment>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                labeled.Add(new LabeledTranscriptionSegment(
                    segments[i],
                    ResolveSpeakerLabel(leftEnergy[i], rightEnergy[i])));
            }
            return labeled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 話者判定は認識結果に対する付加情報であり、original audioの読み取り失敗だけで
            // ASR結果そのものを破棄しない既存仕様を維持する。
            return segments.Select(x => new LabeledTranscriptionSegment(x, null)).ToArray();
        }
    }

    private static IReadOnlyList<SegmentFrameRange> BuildSegmentFrameRanges(
        IReadOnlyList<RecognizedTranscriptionSegment> segments,
        int sampleRate)
    {
        var ranges = new List<SegmentFrameRange>(segments.Count);
        foreach (var segment in segments)
        {
            var startSeconds = Math.Max(0d, segment.Start.TotalSeconds);
            var endSeconds = Math.Max(startSeconds + 0.02d, segment.End.TotalSeconds);
            var startFrame = (long)Math.Floor(startSeconds * sampleRate);
            var endFrame = (long)Math.Ceiling(endSeconds * sampleRate);
            if (endFrame <= startFrame)
            {
                endFrame = startFrame + 1;
            }
            ranges.Add(new SegmentFrameRange(startFrame, endFrame));
        }
        return ranges;
    }

    private static string ResolveSpeakerLabel(double leftEnergy, double rightEnergy)
    {
        const double epsilon = 1e-10;
        const double sameLevelThresholdDb = 2.5;
        var diffDb = 10d * Math.Log10((rightEnergy + epsilon) / (leftEnergy + epsilon));
        if (Math.Abs(diffDb) < sameLevelThresholdDb)
        {
            return "Mixed";
        }
        return diffDb > 0 ? "Mic" : "Speaker";
    }

    private sealed record SegmentFrameRange(long StartFrame, long EndFrame);
}

/// <summary>
/// Engine認識segmentとCommonで付加した話者ラベルを保持する
/// </summary>
public sealed record LabeledTranscriptionSegment(
    RecognizedTranscriptionSegment Segment,
    string? SpeakerLabel);
