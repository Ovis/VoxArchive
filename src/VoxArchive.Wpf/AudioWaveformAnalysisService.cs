using NAudio.Wave;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorの初期表示に使用する全体波形をストリーミング解析する。
/// </summary>
/// <remarks>
/// 長時間録音でもPCM全体を保持しないよう、固定数のbucketへMin/Maxを集約する。
/// 波形表示用の縮約値とは別に、入力PCMの絶対ピークも同じdecode passで保持する。
/// </remarks>
public static class AudioWaveformAnalysisService
{
    private const int DefaultBucketCount = 2400;
    private const int ReadBufferFrames = 4096;

    /// <summary>
    /// 指定ファイルを解析し、全体表示用の粗波形を生成する。
    /// </summary>
    public static Task<AudioWaveformAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => AnalyzeCore(filePath, progress, cancellationToken), cancellationToken);

    private static AudioWaveformAnalysisResult AnalyzeCore(
        string filePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var reader = new AudioFileReader(filePath);
        var channels = reader.WaveFormat.Channels;
        if (channels is < 1 or > 2)
        {
            throw new NotSupportedException("Audio EditorはMonoまたはStereo音声のみを扱います。");
        }

        var sampleRate = reader.WaveFormat.SampleRate;
        var sourceFrameCount = reader.Length / reader.WaveFormat.BlockAlign;
        var bucketCount = sourceFrameCount <= 0
            ? 1
            : (int)Math.Min(DefaultBucketCount, sourceFrameCount);

        var mins = new float[channels][];
        var maxs = new float[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            mins[channel] = Enumerable.Repeat(float.PositiveInfinity, bucketCount).ToArray();
            maxs[channel] = Enumerable.Repeat(float.NegativeInfinity, bucketCount).ToArray();
        }

        var input = new float[ReadBufferFrames * channels];
        long frameIndex = 0;
        double peakAbsoluteSample = 0d;
        long lastReportedFrame = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readSamples = reader.Read(input, 0, input.Length);
            if (readSamples <= 0)
            {
                break;
            }

            var readFrames = readSamples / channels;
            for (var frame = 0; frame < readFrames; frame++, frameIndex++)
            {
                var bucket = sourceFrameCount <= 0
                    ? 0
                    : (int)Math.Min(bucketCount - 1, frameIndex * bucketCount / sourceFrameCount);

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = input[(frame * channels) + channel];
                    if (sample < mins[channel][bucket]) mins[channel][bucket] = sample;
                    if (sample > maxs[channel][bucket]) maxs[channel][bucket] = sample;
                    peakAbsoluteSample = Math.Max(peakAbsoluteSample, Math.Abs((double)sample));
                }
            }

            if (sourceFrameCount > 0 && frameIndex - lastReportedFrame >= sampleRate)
            {
                lastReportedFrame = frameIndex;
                progress?.Report(Math.Clamp((double)frameIndex / sourceFrameCount, 0d, 1d));
            }
        }

        for (var channel = 0; channel < channels; channel++)
        {
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                if (float.IsPositiveInfinity(mins[channel][bucket])) mins[channel][bucket] = 0f;
                if (float.IsNegativeInfinity(maxs[channel][bucket])) maxs[channel][bucket] = 0f;
            }
        }

        progress?.Report(1d);
        var duration = sourceFrameCount <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)sourceFrameCount / sampleRate);
        var envelopes = Enumerable.Range(0, channels)
            .Select(channel => new AudioWaveformEnvelope(mins[channel], maxs[channel]))
            .ToArray();

        return new AudioWaveformAnalysisResult(
            duration,
            sampleRate,
            channels,
            sourceFrameCount,
            peakAbsoluteSample,
            envelopes);
    }
}

/// <summary>
/// 1チャンネル分の波形bucketを表す。
/// </summary>
public sealed record AudioWaveformEnvelope(float[] Minimums, float[] Maximums);

/// <summary>
/// Audio Editorの初期波形解析結果を表す。
/// </summary>
public sealed record AudioWaveformAnalysisResult(
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    long SourceFrameCount,
    double PeakAbsoluteSample,
    IReadOnlyList<AudioWaveformEnvelope> Envelopes);