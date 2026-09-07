using NAudio.Wave;
using SherpaOnnx;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// sherpa-onnxのSilero VADを使用してPrepared Audioから発話区間を検出する
/// </summary>
/// <remarks>
/// このクラスはSilero単体の検出だけを担当する。モデル利用可否の判定、音量ベースVADへのfallback、
/// 連続失敗によるセッション抑制は上位の選択detectorで扱い、正常な0件結果をfallbackと混同しない。
/// </remarks>
public sealed class SileroVadDetector(string modelPath) : ISpeechRegionDetector
{
    private const int RequiredSampleRate = 16_000;
    private const int WindowSize = 512;
    private const float MinimumBufferSeconds = 60f;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            throw new FileNotFoundException("Silero VADモデルが配置されていません。", modelPath);
        }
        if (audio.Format.SampleRate != RequiredSampleRate || audio.Format.Channels != 1)
        {
            throw new InvalidDataException(
                $"Silero VAD入力は16kHz monoである必要があります。実際={audio.Format.SampleRate}Hz/{audio.Format.Channels}ch");
        }

        var options = SileroVadOptions.FromSnapshot(settings);
        var config = CreateConfig(modelPath, options, audio.Duration);
        var rawRegions = new List<AudioSampleRange>();

        // native detectorが保持する発話bufferはMaxSpeechDurationより短いと途中結果を保持できないため、
        // Prepared Audio全体を収容できる値を使う。K2の25秒制約はRecognitionChunkerの責務であり、
        // Silero側のMaxSpeechDurationでSpeechRegionを分割しない。
        var bufferSeconds = Math.Max(MinimumBufferSeconds, (float)Math.Ceiling(audio.Duration.TotalSeconds + 1d));
        using var detector = new VoiceActivityDetector(config, bufferSeconds);
        await using var stream = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(stream);
        ISampleProvider provider = reader.ToSampleProvider();
        ValidateWaveFormat(provider.WaveFormat);

        var window = new float[WindowSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(window.AsSpan());
            if (read <= 0)
            {
                break;
            }

            if (read == window.Length)
            {
                detector.AcceptWaveform(window);
            }
            else
            {
                // 最終端を0で埋めると人工無音がVAD判断へ混入するため、実際に存在するsampleだけを渡す。
                var tail = new float[read];
                Array.Copy(window, tail, read);
                detector.AcceptWaveform(tail);
            }
            DrainSegments(detector, rawRegions);
        }

        detector.Flush();
        DrainSegments(detector, rawRegions);

        var sampleCount = Math.Max(0L, (long)Math.Floor(audio.Duration.TotalSeconds * audio.Format.SampleRate));
        return SileroVadRegionBuilder.Build(rawRegions, sampleCount, audio.Format.SampleRate, options);
    }

    /// <summary>
    /// モデルファイルを実際にロードしてSilero VADを初期化できることを確認する
    /// </summary>
    /// <remarks>
    /// ファイル存在だけでは破損や非互換モデルを検出できないため、利用可能判定ではnative detectorの生成まで行う。
    /// </remarks>
    public static void ValidateModelLoad(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Silero VADモデルが配置されていません。", path);
        }

        var config = CreateConfig(path, SileroVadOptions.Default, TimeSpan.FromSeconds(1));
        using var detector = new VoiceActivityDetector(config, MinimumBufferSeconds);
        detector.Reset();
    }

    private static VadModelConfig CreateConfig(string path, SileroVadOptions options, TimeSpan audioDuration)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = path;
        config.SileroVad.Threshold = (float)options.Threshold;
        config.SileroVad.MinSilenceDuration = options.MinimumSilenceDurationMilliseconds / 1000f;
        config.SileroVad.MinSpeechDuration = options.MinimumSpeechDurationMilliseconds / 1000f;

        // sherpa-onnxではMaxSpeechDurationが必須だが、ここで長さ制限するとVADとASR chunkingの責務が混ざる。
        // 音声全体より長い値を指定し、実質的にVoxArchive側では上限制約として使用しない。
        config.SileroVad.MaxSpeechDuration = Math.Max(60f, (float)Math.Ceiling(audioDuration.TotalSeconds + 1d));
        config.SileroVad.WindowSize = WindowSize;
        config.Debug = 0;
        return config;
    }

    private static void DrainSegments(VoiceActivityDetector detector, ICollection<AudioSampleRange> destination)
    {
        while (!detector.IsEmpty())
        {
            var segment = detector.Front();
            var start = Math.Max(0L, segment.Start);
            var end = start + segment.Samples.LongLength;
            if (end > start)
            {
                destination.Add(new AudioSampleRange(start, end));
            }
            detector.Pop();
        }
    }

    private static void ValidateWaveFormat(WaveFormat format)
    {
        if (format.SampleRate != RequiredSampleRate || format.Channels != 1)
        {
            throw new InvalidDataException(
                $"Silero VAD入力は16kHz monoである必要があります。実際={format.SampleRate}Hz/{format.Channels}ch");
        }
    }
}
