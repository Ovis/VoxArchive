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
        => ApplyCore(audioFilePath, segments, includeDiagnostics: false, cancellationToken).Segments;

    /// <summary>
    /// 既存の話者判定結果に加えて、同じ判定で使用したCH1/CH2エネルギーを診断用に返す
    /// </summary>
    /// <remarks>
    /// 診断のために別計算を行うと通常処理と診断値が食い違う可能性があるため、
    /// ラベル決定に実際に使用した同じ累積値をそのまま返す。
    /// </remarks>
    public SpeakerLabelingDiagnosticResult ApplyWithDiagnostics(
        string audioFilePath,
        IReadOnlyList<RecognizedTranscriptionSegment> segments,
        CancellationToken cancellationToken = default)
        => ApplyCore(audioFilePath, segments, includeDiagnostics: true, cancellationToken);

    private static SpeakerLabelingDiagnosticResult ApplyCore(
        string audioFilePath,
        IReadOnlyList<RecognizedTranscriptionSegment> segments,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return new SpeakerLabelingDiagnosticResult([], []);
        }

        try
        {
            using var reader = new AudioFileReader(audioFilePath);
            if (reader.WaveFormat.Channels < 2)
            {
                var unlabeled = segments.Select(x => new LabeledTranscriptionSegment(x, null)).ToArray();
                var diagnostics = includeDiagnostics
                    ? segments.Select((x, index) => new SpeakerLabelingDiagnosticTrace(
                        x.RecognitionChunkId ?? index,
                        SpeakerLabel: null,
                        SpeakerChannelEnergy: null,
                        MicrophoneChannelEnergy: null)).ToArray()
                    : [];
                return new SpeakerLabelingDiagnosticResult(unlabeled, diagnostics);
            }

            ISampleProvider sampleProvider = reader;
            var sampleRate = sampleProvider.WaveFormat.SampleRate;
            var channels = sampleProvider.WaveFormat.Channels;
            var ranges = BuildSegmentFrameRanges(segments, sampleRate);
            var leftEnergy = new double[segments.Count];
            var rightEnergy = new double[segments.Count];
            var buffer = new float[Math.Max(4096, sampleRate / 4) * channels];
            var segmentIndex = 0;
            long frameIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = sampleProvider.Read(buffer.AsSpan());
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
            var traces = includeDiagnostics ? new List<SpeakerLabelingDiagnosticTrace>(segments.Count) : null;
            for (var i = 0; i < segments.Count; i++)
            {
                var label = ResolveSpeakerLabel(leftEnergy[i], rightEnergy[i]);
                labeled.Add(new LabeledTranscriptionSegment(segments[i], label));
                traces?.Add(new SpeakerLabelingDiagnosticTrace(
                    segments[i].RecognitionChunkId ?? i,
                    label,
                    leftEnergy[i],
                    rightEnergy[i]));
            }
            return new SpeakerLabelingDiagnosticResult(labeled, traces ?? []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 話者判定は認識結果に対する付加情報であり、original audioの読み取り失敗だけで
            // ASR結果そのものを破棄しない既存仕様を維持する。診断値も推測せずnullとして残す。
            var unlabeled = segments.Select(x => new LabeledTranscriptionSegment(x, null)).ToArray();
            var diagnostics = includeDiagnostics
                ? segments.Select((x, index) => new SpeakerLabelingDiagnosticTrace(
                    x.RecognitionChunkId ?? index,
                    SpeakerLabel: null,
                    SpeakerChannelEnergy: null,
                    MicrophoneChannelEnergy: null)).ToArray()
                : [];
            return new SpeakerLabelingDiagnosticResult(unlabeled, diagnostics);
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

/// <summary>
/// 1 segmentの話者判定結果と、その判定に使用したCH1/CH2エネルギーを保持する
/// </summary>
public sealed record SpeakerLabelingDiagnosticTrace(
    int RecognitionChunkId,
    string? SpeakerLabel,
    double? SpeakerChannelEnergy,
    double? MicrophoneChannelEnergy);

/// <summary>
/// 話者ラベル付きsegmentと診断根拠を同じ処理結果として返す
/// </summary>
public sealed record SpeakerLabelingDiagnosticResult(
    IReadOnlyList<LabeledTranscriptionSegment> Segments,
    IReadOnlyList<SpeakerLabelingDiagnosticTrace> Traces);
