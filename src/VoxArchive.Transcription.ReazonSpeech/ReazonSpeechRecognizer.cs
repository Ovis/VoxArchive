using NAudio.Wave;
using SherpaOnnx;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech k2-v2をsherpa-onnx non-streaming recognizerへ接続する
/// </summary>
public sealed class ReazonSpeechRecognizer
{
    private const int ModelSampleRate = 16_000;
    private const int FeatureDimension = 80;
    private const string TokenTimestampTraceMetadataKey = "k2TokenTimestampTrace";

    /// <summary>
    /// 指定したRecognitionChunkを順次認識する
    /// </summary>
    public async Task<IReadOnlyList<RecognizedTranscriptionSegment>> RecognizeAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<RecognitionChunk> chunks,
        ReazonSpeechEngineOptions options,
        bool diagnosticsEnabled,
        CancellationToken cancellationToken = default)
    {
        ValidateModelFiles(options);
        var config = CreateRecognizerConfig(options, diagnosticsEnabled);

        // ONNXモデルのロードは高コストなので、RecognitionChunkごとにRecognizerを作り直さず1 Jobで共有する。
        using var recognizer = new OfflineRecognizer(config);
        var segments = new List<RecognizedTranscriptionSegment>(chunks.Count);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = await ReadChunkSamplesAsync(audio, chunk, cancellationToken);
            if (samples.Length == 0)
            {
                continue;
            }

            // ReazonSpeech K2 v2は入力前後の短い無音を前提とするため、Adapter境界で固定0.9秒を付加する。
            // VAD paddingやRecognitionChunkのoriginal timelineは変更せず、K2へ渡す一時入力だけを拡張する。
            var k2InputSamples = ReazonSpeechK2InputPadding.Apply(samples);

            // Decodeはnative同期APIであり呼び出し途中を安全に強制停止できない。
            // safe boundaryであるchunk間ではCancellationTokenを必ず確認し、UIスレッド自体はTask.Runで塞がない。
            var recognition = await Task.Run(
                () => Recognize(recognizer, k2InputSamples),
                CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(recognition.Text))
            {
                continue;
            }

            IReadOnlyDictionary<string, object?>? metadata = null;
            if (diagnosticsEnabled)
            {
                // ReazonSpeech公式K2 APIのtimestampはsubwordごとの単一点であり、segmentの終了時刻は提供されない。
                // 存在しないrangeを推測してcanonical時刻へ混ぜず、raw→補正後sampleのtraceだけを診断用metadataへ保持する。
                // 通常ログにはtoken/textを出さず、診断OFF時はtrace生成自体を避ける。
                var tokenTimestampTrace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
                    recognition.Tokens,
                    recognition.Timestamps,
                    chunk);
                metadata = new Dictionary<string, object?>
                {
                    [TokenTimestampTraceMetadataKey] = tokenTimestampTrace
                };
            }

            var start = SamplesToTimeSpan(chunk.StartSample, audio.Format.SampleRate);
            var end = SamplesToTimeSpan(chunk.EndSample, audio.Format.SampleRate);
            segments.Add(new RecognizedTranscriptionSegment(
                start,
                end,
                recognition.Text.Trim(),
                chunk.RecognitionChunkId,
                metadata));
        }
        return segments;
    }

    /// <summary>
    /// Job開始時に確定したReazonSpeech設定からsherpa-onnx recognizer設定を生成する
    /// </summary>
    /// <remarks>
    /// 値の妥当性はAdmission前のsettings validationで確認済みとし、ここではclampや既定値への置換を行わない。
    /// 実行時に保存値を暗黙補正すると診断JSONのsnapshotと実際の推論条件が一致しなくなるためである。
    /// </remarks>
    internal static OfflineRecognizerConfig CreateRecognizerConfig(ReazonSpeechEngineOptions options, bool diagnosticsEnabled)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = ModelSampleRate;
        config.FeatConfig.FeatureDim = FeatureDimension;
        config.ModelConfig.Transducer.Encoder = options.EncoderPath!;
        config.ModelConfig.Transducer.Decoder = options.DecoderPath!;
        config.ModelConfig.Transducer.Joiner = options.JoinerPath!;
        config.ModelConfig.Tokens = options.TokensPath!;

        // 今フェーズではReazonSpeechはCPU固定とし、threads/decodingだけを利用者設定から反映する。
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = options.CpuThreads;
        config.ModelConfig.Debug = diagnosticsEnabled ? 1 : 0;
        config.DecodingMethod = options.DecodingMethod switch
        {
            ReazonSpeechDecodingMethod.GreedySearch => "greedy_search",
            ReazonSpeechDecodingMethod.ModifiedBeamSearch => "modified_beam_search",
            _ => throw new ArgumentOutOfRangeException(nameof(options.DecodingMethod), options.DecodingMethod, "未対応のReazonSpeech decoding methodです。")
        };
        config.MaxActivePaths = options.MaxActivePaths;
        return config;
    }

    private static ReazonSpeechNativeRecognitionResult Recognize(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(ModelSampleRate, samples);
        recognizer.Decode(stream);
        var result = stream.Result;

        // OfflineStreamの破棄後にnative領域へ依存しないよう、必要な結果をmanaged配列へコピーしてから返す。
        // token timestampは診断ON時だけ利用するが、Decode境界でまとめてcopyしてlifetimeを明確にする。
        return new ReazonSpeechNativeRecognitionResult(
            result.Text ?? string.Empty,
            result.Tokens?.ToArray() ?? [],
            result.Timestamps?.ToArray() ?? []);
    }

    private static async Task<float[]> ReadChunkSamplesAsync(
        IPreparedTranscriptionAudio audio,
        RecognitionChunk chunk,
        CancellationToken cancellationToken)
    {
        await using var source = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(source);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.SampleRate != ModelSampleRate || provider.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"ReazonSpeech入力は16kHz monoである必要があります。実際={provider.WaveFormat.SampleRate}Hz/{provider.WaveFormat.Channels}ch");
        }

        // RecognitionChunkはPrepared Audio上のsample座標を正本とするため、秒への往復変換を挟まず直接切り出す。
        var skipSamples = Math.Max(0L, chunk.StartSample);
        var requestedSamples = checked((int)Math.Min(int.MaxValue, Math.Max(0L, chunk.Length)));
        var scratch = new float[8192];
        while (skipSamples > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(skipSamples, scratch.Length);
            var read = provider.Read(scratch.AsSpan(0, toRead));
            if (read <= 0)
            {
                return [];
            }
            skipSamples -= read;
        }

        var samples = new float[requestedSamples];
        var offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(samples.AsSpan(offset));
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }

        if (offset != samples.Length)
        {
            Array.Resize(ref samples, offset);
        }
        return samples;
    }

    private static TimeSpan SamplesToTimeSpan(long samples, int sampleRate)
        => TimeSpan.FromSeconds(samples / (double)sampleRate);

    private static void ValidateModelFiles(ReazonSpeechEngineOptions options)
    {
        var files = new[] { options.EncoderPath, options.DecoderPath, options.JoinerPath, options.TokensPath };
        if (files.Any(string.IsNullOrWhiteSpace) || files.Any(path => !File.Exists(path)))
        {
            throw new FileNotFoundException("ReazonSpeechモデルを構成するファイルが未配置または不完全です。");
        }
    }

    /// <summary>
    /// native OfflineRecognizerResultから文字列とtimestamp情報をmanaged lifetimeへ切り離して保持する
    /// </summary>
    private sealed record ReazonSpeechNativeRecognitionResult(
        string Text,
        IReadOnlyList<string> Tokens,
        IReadOnlyList<float> Timestamps);
}
