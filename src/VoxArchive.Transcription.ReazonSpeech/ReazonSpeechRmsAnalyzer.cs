using NAudio.Wave;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2向けchunk境界判定に使用するRMS系列とCoreRanges由来P20を計算する
/// </summary>
/// <remarks>
/// 30ms window / 10ms hopはchunking固有の解析条件として固定する。
/// 長時間録音を全量float配列へ展開しないよう、対象SpeechRegionだけをWaveFileReaderから順次読み取る。
/// </remarks>
internal static class ReazonSpeechRmsAnalyzer
{
    internal const int RequiredSampleRate = 16_000;
    internal const int WindowSamples = 480;
    internal const int HopSamples = 160;
    private const double Percentile = 0.20d;

    /// <summary>
    /// 指定SpeechRegionのRMS系列とCoreRanges内フレームから求めたP20を生成する
    /// </summary>
    internal static async Task<ReazonSpeechRmsAnalysis> AnalyzeAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegion speechRegion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speechRegion);

        if (audio.Format.SampleRate != RequiredSampleRate || audio.Format.Channels != 1)
        {
            throw new InvalidDataException(
                $"ReazonSpeech chunking入力は16kHz monoである必要があります。実際={audio.Format.SampleRate}Hz/{audio.Format.Channels}ch");
        }
        if (speechRegion.EndSample <= speechRegion.StartSample)
        {
            throw new InvalidDataException("SpeechRegionのsample範囲が不正です。");
        }

        await using var stream = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(stream);
        if (reader.WaveFormat.SampleRate != RequiredSampleRate || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"Prepared AudioのWAV形式が16kHz monoではありません。実際={reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch");
        }

        // WaveFileReader.Positionはdata chunk先頭からのbyte位置なので、sample位置をBlockAlignで正確にbyte位置へ変換できる。
        // CurrentTime経由では丸め誤差が入り得るため、canonical sample timelineとの境界一致を優先する。
        reader.Position = checked(speechRegion.StartSample * reader.WaveFormat.BlockAlign);
        ISampleProvider provider = reader.ToSampleProvider();

        var frames = AnalyzeFrames(provider, speechRegion, cancellationToken);
        if (frames.Count == 0)
        {
            return new ReazonSpeechRmsAnalysis([], 0d);
        }

        var coreValues = frames
            .Where(frame => IsFullyInsideAnyCoreRange(frame, speechRegion.CoreRanges))
            .Select(frame => frame.Rms)
            .OrderBy(value => value)
            .ToArray();
        if (coreValues.Length == 0)
        {
            throw new InvalidDataException("SpeechRegion.CoreRanges内にRMS解析可能な30msフレームがありません。");
        }

        var percentileIndex = (int)Math.Floor((coreValues.Length - 1) * Percentile);
        return new ReazonSpeechRmsAnalysis(frames, coreValues[percentileIndex]);
    }

    private static IReadOnlyList<ReazonSpeechRmsFrame> AnalyzeFrames(
        ISampleProvider provider,
        SpeechRegion speechRegion,
        CancellationToken cancellationToken)
    {
        if (speechRegion.Length < WindowSamples)
        {
            return [];
        }

        var window = new float[WindowSamples];
        if (!ReadExact(provider, window, 0, WindowSamples, cancellationToken))
        {
            return [];
        }

        var frames = new List<ReazonSpeechRmsFrame>();
        var frameStart = speechRegion.StartSample;
        while (frameStart + WindowSamples <= speechRegion.EndSample)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(new ReazonSpeechRmsFrame(
                frameStart,
                frameStart + WindowSamples,
                CalculateRms(window)));

            var nextFrameStart = frameStart + HopSamples;
            if (nextFrameStart + WindowSamples > speechRegion.EndSample)
            {
                break;
            }

            // 30ms windowを10msずつ送るため、重複する後半20msを再利用し、新しい10msだけ読む。
            // 同じSpeechRegionを何度もseek/readしないことで長い発話でもI/Oを線形に保つ。
            Array.Copy(window, HopSamples, window, 0, WindowSamples - HopSamples);
            if (!ReadExact(
                    provider,
                    window,
                    WindowSamples - HopSamples,
                    HopSamples,
                    cancellationToken))
            {
                break;
            }
            frameStart = nextFrameStart;
        }

        return frames;
    }

    private static bool ReadExact(
        ISampleProvider provider,
        float[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    private static bool IsFullyInsideAnyCoreRange(
        ReazonSpeechRmsFrame frame,
        IReadOnlyList<AudioSampleRange> coreRanges)
        => coreRanges.Any(core => frame.StartSample >= core.StartSample && frame.EndSample <= core.EndSample);

    private static double CalculateRms(IReadOnlyList<float> samples)
    {
        var sumSquares = 0d;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i];
            sumSquares += value * value;
        }
        return Math.Sqrt(sumSquares / samples.Count);
    }
}

/// <summary>SpeechRegion内の1つのRMS解析フレームを表す</summary>
internal readonly record struct ReazonSpeechRmsFrame(long StartSample, long EndSample, double Rms);

/// <summary>SpeechRegion単位で再利用するRMS系列と低音量閾値を保持する</summary>
internal sealed record ReazonSpeechRmsAnalysis(
    IReadOnlyList<ReazonSpeechRmsFrame> Frames,
    double P20Rms);
