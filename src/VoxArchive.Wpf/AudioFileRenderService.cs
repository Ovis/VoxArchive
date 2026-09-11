using System.IO;
using NAudio.Wave;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorの共通DSPを実ファイルへ接続し、解析とWAVレンダリングを行う。
/// </summary>
/// <remarks>
/// PCM全体は保持せず、KeepRangeをチャンク単位で読み出す。Cut接続点では最大5ms分の
/// 左右バッファだけを保持してCrossfadeする。Clipping解析と書き出しは同一経路を2パスで実行する。
/// </remarks>
public static class AudioFileRenderService
{
    private const int ReadBufferFrames = 4096;
    private delegate void SampleEmitter(float[] samples, int sampleCount);

    public sealed record RenderResult(
        int SampleRate,
        int InputChannels,
        int OutputChannels,
        long SourceFrameCount,
        long OutputFrameCount,
        double PeakAbsoluteSample,
        double AppliedMasterGainDb,
        bool AutoAttenuated);

    /// <summary>
    /// 編集状態を反映して32bit float WAVを書き出す。
    /// </summary>
    public static Task<RenderResult> RenderWaveAsync(
        string inputFilePath,
        string outputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        double masterGainDb = 0d,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RenderWaveCore(
            inputFilePath,
            outputFilePath,
            state,
            channelMode,
            masterGainDb,
            cancellationToken), cancellationToken);
    }

    /// <summary>
    /// ファイルを書き出さず、編集後のピークと必要なMaster減衰量だけを解析する。
    /// </summary>
    public static Task<AudioClippingAnalysis> AnalyzeAsync(
        string inputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var reader = OpenAndValidate(inputFilePath, state);
            var plan = AudioRenderPlan.Create(state, reader.WaveFormat.SampleRate);
            var analysis = new AudioClippingAnalysis();
            StreamRenderedSamples(
                reader,
                state,
                plan,
                channelMode,
                0d,
                (samples, count) => analysis.Observe(samples.AsSpan(0, count)),
                cancellationToken);
            return analysis;
        }, cancellationToken);
    }

    private static RenderResult RenderWaveCore(
        string inputFilePath,
        string outputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        double masterGainDb,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        AudioClippingAnalysis analysis;
        int sampleRate;
        int inputChannels;
        AudioRenderPlan plan;

        using (var analysisReader = OpenAndValidate(inputFilePath, state))
        {
            sampleRate = analysisReader.WaveFormat.SampleRate;
            inputChannels = analysisReader.WaveFormat.Channels;
            plan = AudioRenderPlan.Create(state, sampleRate);
            analysis = new AudioClippingAnalysis();
            StreamRenderedSamples(
                analysisReader,
                state,
                plan,
                channelMode,
                0d,
                (samples, count) => analysis.Observe(samples.AsSpan(0, count)),
                cancellationToken);
        }

        var appliedMasterGainDb = analysis.GetSafeMasterGainDb(masterGainDb);
        var outputChannels = AudioFrameProcessor.GetOutputChannelCount(inputChannels, channelMode);
        var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outputChannels);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputFilePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using var renderReader = OpenAndValidate(inputFilePath, state);
            using var writer = new WaveFileWriter(outputFilePath, outputFormat);
            StreamRenderedSamples(
                renderReader,
                state,
                plan,
                channelMode,
                appliedMasterGainDb,
                (samples, count) => writer.WriteSamples(samples, 0, count),
                cancellationToken);
        }
        catch
        {
            TryDelete(outputFilePath);
            throw;
        }

        var crossfadeFrames = plan.Junctions.Sum(x => (long)x.CrossfadeFrameCount);
        return new RenderResult(
            sampleRate,
            inputChannels,
            outputChannels,
            plan.SourceFrameCount,
            Math.Max(0, plan.KeptFrameCount - crossfadeFrames),
            analysis.PeakAbsoluteSample,
            appliedMasterGainDb,
            appliedMasterGainDb < masterGainDb - 0.0000001d);
    }

    private static AudioFileReader OpenAndValidate(string inputFilePath, AudioEditState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentNullException.ThrowIfNull(state);

        var reader = new AudioFileReader(inputFilePath);
        var channels = reader.WaveFormat.Channels;
        if (channels is < 1 or > 2)
        {
            reader.Dispose();
            throw new NotSupportedException("Audio EditorはMonoまたはStereo音声のみを扱います。");
        }

        if (channels != state.ChannelCount)
        {
            reader.Dispose();
            throw new InvalidOperationException("編集状態のチャンネル数が入力ファイルと一致しません。");
        }

        return reader;
    }

    /// <summary>
    /// Cut / Crossfade / Gain / Mute / Mixdown / Master Gainを共通順序で適用し、結果を逐次通知する。
    /// </summary>
    private static void StreamRenderedSamples(
        AudioFileReader reader,
        AudioEditState state,
        AudioRenderPlan plan,
        AudioRenderChannelMode channelMode,
        double masterGainDb,
        SampleEmitter emit,
        CancellationToken cancellationToken)
    {
        var outputChannels = AudioFrameProcessor.GetOutputChannelCount(reader.WaveFormat.Channels, channelMode);
        float[]? previousTail = null;

        for (var rangeIndex = 0; rangeIndex < plan.KeepRanges.Count; rangeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = plan.KeepRanges[rangeIndex];
            var incomingFadeFrames = rangeIndex == 0 ? 0 : plan.Junctions[rangeIndex - 1].CrossfadeFrameCount;
            var outgoingFadeFrames = rangeIndex + 1 < plan.KeepRanges.Count ? plan.Junctions[rangeIndex].CrossfadeFrameCount : 0;

            var cursor = range.StartFrame;
            if (incomingFadeFrames > 0)
            {
                var head = ReadProcessedSegment(
                    reader,
                    state,
                    channelMode,
                    cursor,
                    incomingFadeFrames,
                    cancellationToken);

                if (previousTail is null || previousTail.Length != head.Length)
                {
                    throw new InvalidOperationException("Crossfade境界のバッファ長が一致しません。");
                }

                var mixed = new float[head.Length];
                AudioCrossfadeMixer.Mix(previousTail, head, mixed, outputChannels);
                ApplyMasterGainInPlace(mixed, masterGainDb);
                emit(mixed, mixed.Length);
                previousTail = null;
                cursor += incomingFadeFrames;
            }

            var bodyEnd = range.EndFrameExclusive - outgoingFadeFrames;
            if (bodyEnd > cursor)
            {
                StreamProcessedSegment(
                    reader,
                    state,
                    channelMode,
                    cursor,
                    bodyEnd - cursor,
                    masterGainDb,
                    emit,
                    cancellationToken);
            }

            if (outgoingFadeFrames > 0)
            {
                previousTail = ReadProcessedSegment(
                    reader,
                    state,
                    channelMode,
                    bodyEnd,
                    outgoingFadeFrames,
                    cancellationToken);
            }
        }

        if (previousTail is not null)
        {
            ApplyMasterGainInPlace(previousTail, masterGainDb);
            emit(previousTail, previousTail.Length);
        }
    }

    private static void StreamProcessedSegment(
        AudioFileReader reader,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        long startFrame,
        long frameCount,
        double masterGainDb,
        SampleEmitter emit,
        CancellationToken cancellationToken)
    {
        if (frameCount <= 0)
        {
            return;
        }

        var inputChannels = reader.WaveFormat.Channels;
        var outputChannels = AudioFrameProcessor.GetOutputChannelCount(inputChannels, channelMode);
        var input = new float[ReadBufferFrames * inputChannels];
        var output = new float[ReadBufferFrames * outputChannels];
        SeekToFrame(reader, startFrame);

        var remainingFrames = frameCount;
        while (remainingFrames > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wantedFrames = (int)Math.Min(ReadBufferFrames, remainingFrames);
            var wantedSamples = wantedFrames * inputChannels;
            var readSamples = ReadExactlyUpTo(reader, input, wantedSamples);
            var readFrames = readSamples / inputChannels;
            if (readFrames <= 0)
            {
                throw new EndOfStreamException("入力音声がレンダリング計画より短いため読み込みを継続できません。ほかのアプリによるファイル更新を確認してください。");
            }

            var outputSampleCount = ProcessFrames(
                input.AsSpan(0, readFrames * inputChannels),
                output,
                state.Channels,
                channelMode,
                masterGainDb,
                inputChannels,
                outputChannels);
            emit(output, outputSampleCount);
            remainingFrames -= readFrames;
        }
    }

    private static float[] ReadProcessedSegment(
        AudioFileReader reader,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        long startFrame,
        int frameCount,
        CancellationToken cancellationToken)
    {
        if (frameCount <= 0)
        {
            return Array.Empty<float>();
        }

        var inputChannels = reader.WaveFormat.Channels;
        var outputChannels = AudioFrameProcessor.GetOutputChannelCount(inputChannels, channelMode);
        var input = new float[frameCount * inputChannels];
        SeekToFrame(reader, startFrame);
        var readSamples = ReadExactlyUpTo(reader, input, input.Length);
        var readFrames = readSamples / inputChannels;
        if (readFrames != frameCount)
        {
            throw new EndOfStreamException("Crossfadeに必要なサンプルを入力音声から読み込めませんでした。");
        }

        var output = new float[frameCount * outputChannels];
        ProcessFrames(
            input,
            output,
            state.Channels,
            channelMode,
            0d,
            inputChannels,
            outputChannels);
        return output;
    }

    private static int ProcessFrames(
        ReadOnlySpan<float> input,
        Span<float> output,
        IReadOnlyList<AudioChannelEditState> channelStates,
        AudioRenderChannelMode channelMode,
        double masterGainDb,
        int inputChannels,
        int outputChannels)
    {
        var frames = input.Length / inputChannels;
        for (var frame = 0; frame < frames; frame++)
        {
            AudioFrameProcessor.ProcessFrame(
                input.Slice(frame * inputChannels, inputChannels),
                output.Slice(frame * outputChannels, outputChannels),
                channelStates,
                channelMode,
                masterGainDb);
        }

        return frames * outputChannels;
    }

    private static int ReadExactlyUpTo(AudioFileReader reader, float[] buffer, int requestedSamples)
    {
        var total = 0;
        while (total < requestedSamples)
        {
            var read = reader.Read(buffer, total, requestedSamples - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void SeekToFrame(AudioFileReader reader, long frame)
    {
        var bytePosition = checked(frame * reader.WaveFormat.BlockAlign);
        reader.Position = Math.Min(reader.Length, bytePosition);
    }

    private static void ApplyMasterGainInPlace(Span<float> samples, double masterGainDb)
    {
        if (masterGainDb == 0d)
        {
            return;
        }

        var gain = double.IsNegativeInfinity(masterGainDb) ? 0d : Math.Pow(10d, masterGainDb / 20d);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(samples[i] * gain);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
