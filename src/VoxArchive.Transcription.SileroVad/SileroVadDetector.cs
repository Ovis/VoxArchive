using System.Text.Json;
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
public sealed class SileroVadDetector(string modelPath) : IDiagnosticSpeechRegionDetector
{
    private const int RequiredSampleRate = 16_000;
    private const int WindowSize = 512;
    private const float MinimumBufferSeconds = 60f;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
        => (await DetectWithDiagnosticsAsync(audio, settings, cancellationToken)).SpeechRegions;

    /// <inheritdoc />
    public async Task<SpeechRegionDetectionDiagnosticResult> DetectWithDiagnosticsAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            throw new SileroVadUnavailableException("Silero VADモデルが配置されていません。");
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
        VoiceActivityDetector detector;
        try
        {
            detector = new VoiceActivityDetector(config, bufferSeconds);
        }
        catch (Exception ex)
        {
            // native detectorを生成できない段階は推論失敗ではなくモデル利用不可として扱う。
            // 上位selectorはこの区別を使って、連続推論失敗によるsession suppressionを誤って進めない。
            throw new SileroVadUnavailableException("Silero VADモデルを初期化できませんでした。", ex);
        }

        using (detector)
        {
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

                if (read < window.Length)
                {
                    // sherpa-onnxのFlushはSilero内部に残ったpartial inference window自体を推論する処理ではない。
                    // EOFの実sampleだけを渡すと末尾発話が判定されない可能性があるため、この最終windowに限って
                    // 必要最小限の0を補い512 sampleを成立させる。補ったsampleはVAD推論専用でcanonical timelineには含めない。
                    PadEofWindow(window, read);
                }

                detector.AcceptWaveform(window);
                DrainSegments(detector, rawRegions);

                if (read < window.Length)
                {
                    // ISampleProviderではshort readがEOFを表すため、人工0を含むwindowを一度だけ渡して終了する。
                    break;
                }
            }

            detector.Flush();
            DrainSegments(detector, rawRegions);
        }

        // EOF paddingでnative側のsegmentが実音声末尾を越える可能性があるため、raw段階でPrepared Audioへclampする。
        // artificial sampleを診断JSONや後段のcanonical timelineへ漏らさないことが目的であり、Durationから再計算しない。
        var realRawRegions = ClampRawRegions(rawRegions, audio.SampleCount);
        var speechRegions = SileroVadRegionBuilder.Build(realRawRegions, audio.SampleCount, audio.Format.SampleRate, options);
        var diagnosticRawRegions = realRawRegions
            .Select((range, index) => new SpeechRegionDetectionRawRegion(index, range.StartSample, range.EndSample))
            .ToArray();
        return new SpeechRegionDetectionDiagnosticResult(
            speechRegions,
            new SpeechRegionDetectionDiagnosticTrace(
                "SileroVad",
                diagnosticRawRegions,
                FallbackUsed: false,
                FallbackReason: null,
                EffectiveSettings: JsonSerializer.SerializeToElement(new
                {
                    threshold = options.Threshold,
                    minimumSpeechDurationMilliseconds = options.MinimumSpeechDurationMilliseconds,
                    minimumSilenceDurationMilliseconds = options.MinimumSilenceDurationMilliseconds,
                    prePaddingMilliseconds = options.PrePaddingMilliseconds,
                    postPaddingMilliseconds = options.PostPaddingMilliseconds
                })));
    }

    /// <summary>
    /// EOFのpartial windowをSilero推論に必要なwindow長へ0埋めする
    /// </summary>
    /// <param name="window">実sampleが先頭から格納されたSilero入力window</param>
    /// <param name="realSampleCount">window内に存在する実Prepared Audio sample数</param>
    /// <returns>推論専用に追加した0 sample数</returns>
    internal static int PadEofWindow(Span<float> window, int realSampleCount)
    {
        if (realSampleCount < 0 || realSampleCount > window.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(realSampleCount));
        }
        if (realSampleCount == 0 || realSampleCount == window.Length)
        {
            return 0;
        }

        window[realSampleCount..].Clear();
        return window.Length - realSampleCount;
    }

    /// <summary>
    /// native VADが返したraw regionを実Prepared Audioのsample範囲へ制限する
    /// </summary>
    /// <param name="ranges">native VADが返したraw region</param>
    /// <param name="sampleCount">Prepared Audioに実在するsample数</param>
    internal static IReadOnlyList<AudioSampleRange> ClampRawRegions(
        IEnumerable<AudioSampleRange> ranges,
        long sampleCount)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (sampleCount <= 0)
        {
            return [];
        }

        return ranges
            .Select(range => new AudioSampleRange(
                Math.Clamp(range.StartSample, 0L, sampleCount),
                Math.Clamp(range.EndSample, 0L, sampleCount)))
            .Where(range => range.EndSample > range.StartSample)
            .ToArray();
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
