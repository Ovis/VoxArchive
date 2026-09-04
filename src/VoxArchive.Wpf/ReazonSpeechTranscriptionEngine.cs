using System.IO;
using NAudio.Wave;
using SherpaOnnx;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// ReazonSpeech k2-v2 を sherpa-onnx の非ストリーミング認識APIへ接続する
/// </summary>
/// <remarks>
/// 音声正規化・VAD・話者ラベル付与・canonical document生成はWhisperと同じ共通サービスを利用し、
/// 本クラスにはReazonSpeech固有のモデル解決とsherpa-onnx認識処理だけを残す。
/// 初期実装はCPU固定とし、GPU実行方式は実測後にEngine固有設定として追加する。
/// </remarks>
public sealed class ReazonSpeechTranscriptionEngine(
    ReazonSpeechModelProvider modelProvider,
    TranscriptionAudioPreparationService audioPreparationService,
    TranscriptionSpeechRegionDetector speechRegionDetector,
    TranscriptionSpeakerLabelService speakerLabelService,
    TranscriptionDocumentStore documentStore,
    TranscriptionExportService exportService) : ITranscriptionEngine
{
    private const int ModelSampleRate = 16_000;
    private const int FeatureDimension = 80;

    /// <inheritdoc />
    public async Task<TranscriptionJobResult> TranscribeAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = DateTimeOffset.Now;

        try
        {
            ValidateRequest(request);
            if (!File.Exists(request.AudioFilePath))
            {
                return Fail("対象ファイルが見つかりません。", started);
            }

            if (!modelProvider.IsInstalled(request.ModelId))
            {
                return Fail("ReazonSpeechモデルが未配置または不完全です。設定画面からモデルを取得してください。", started);
            }

            var definition = modelProvider.GetDefinition(request.ModelId);
            var installation = modelProvider.GetInstallation(request.ModelId);
            var modelFiles = ResolveModelFiles(installation);

            await using var preparedAudio = await audioPreparationService.PrepareAsync(
                request.AudioFilePath,
                request.Options.DefaultSpeakerPlaybackGainDb,
                request.Options.DefaultMicPlaybackGainDb,
                cancellationToken);

            var speechRegions = await speechRegionDetector.DetectAsync(preparedAudio.WaveFilePath, cancellationToken);
            var recognizedSegments = speechRegions.Count == 0
                ? Array.Empty<TranscribedSegment>()
                : await RecognizeRegionsAsync(preparedAudio.WaveFilePath, speechRegions, modelFiles, request, cancellationToken);

            var labeledSegments = await Task.Run(
                () => speakerLabelService.Apply(request.AudioFilePath, recognizedSegments, cancellationToken),
                cancellationToken);

            var finished = DateTimeOffset.Now;
            var documentPath = BuildDocumentPath(request.AudioFilePath, request.EngineId, request.ModelId);
            var document = BuildDocument(request, definition, labeledSegments, finished);
            await documentStore.SaveAsync(documentPath, document, cancellationToken);

            // JSONは常に正本として保存する。利用者が選択したTXT/SRT/VTTだけを正本から派生生成する。
            var derivedFormats = request.Options.TranscriptionOutputFormats
                & (TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt | TranscriptionOutputFormats.Vtt);
            var derivedFiles = await exportService.WriteDerivedAsync(documentPath, document, derivedFormats, cancellationToken);
            var generatedFiles = new[] { documentPath }.Concat(derivedFiles).ToArray();

            return new TranscriptionJobResult(
                true,
                "ReazonSpeechによる文字起こしが完了しました。",
                generatedFiles,
                started,
                finished);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"ReazonSpeech文字起こしに失敗しました: {ex.Message}", started);
        }
    }

    private static void ValidateRequest(TranscriptionJobRequest request)
    {
        if (request.EngineId != TranscriptionEngineId.ReazonSpeech)
        {
            throw new InvalidOperationException(
                $"ReazonSpeechエンジンへ異なるEngine IDのRequestが渡されました: {request.EngineId}");
        }

        if (request.ModelId != ReazonSpeechModelCatalog.JapaneseModelId)
        {
            throw new NotSupportedException($"未対応のReazonSpeechモデルです: {request.ModelId}");
        }
    }

    private static async Task<IReadOnlyList<TranscribedSegment>> RecognizeRegionsAsync(
        string waveFilePath,
        IReadOnlyList<SpeechRegion> speechRegions,
        ReazonSpeechModelFiles modelFiles,
        TranscriptionJobRequest request,
        CancellationToken cancellationToken)
    {
        var config = CreateRecognizerConfig(modelFiles, request.Options.TranscriptionDiagnosticsLogEnabled);

        // sherpa-onnxのRecognizer生成ではONNXモデルをロードするため、VAD区間ごとに作り直さず
        // 1ジョブ中は同じRecognizerを共有してモデル初期化コストを繰り返さない。
        using var recognizer = new OfflineRecognizer(config);
        var segments = new List<TranscribedSegment>(speechRegions.Count);

        foreach (var region in speechRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = await Task.Run(
                () => ReadRegionSamples(waveFilePath, region, cancellationToken),
                cancellationToken);
            if (samples.Length == 0)
            {
                continue;
            }

            // Decodeはnative同期APIのため呼び出し中の強制キャンセルはできない。
            // UIスレッドを塞がないようバックグラウンドで実行し、区間間ではCancellationTokenを必ず確認する。
            var text = await Task.Run(() => Recognize(recognizer, samples), cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            segments.Add(new TranscribedSegment(region.Start, region.End, text.Trim()));
        }

        return segments;
    }

    private static OfflineRecognizerConfig CreateRecognizerConfig(
        ReazonSpeechModelFiles files,
        bool diagnosticsEnabled)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = ModelSampleRate;
        config.FeatConfig.FeatureDim = FeatureDimension;
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;

        // 初期対応では実行環境差を最小化して認識品質とモデル互換性を検証することを優先する。
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Debug = diagnosticsEnabled ? 1 : 0;
        config.DecodingMethod = "greedy_search";
        return config;
    }

    private static string Recognize(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(ModelSampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text ?? string.Empty;
    }

    private static float[] ReadRegionSamples(
        string waveFilePath,
        SpeechRegion region,
        CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(waveFilePath);
        if (reader.WaveFormat.SampleRate != ModelSampleRate || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"ReazonSpeech入力は16kHzモノラルである必要があります。実際: {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch");
        }

        reader.CurrentTime = region.Start;
        var requestedSamples = Math.Max(0, (int)Math.Ceiling(region.Duration.TotalSeconds * ModelSampleRate));
        if (requestedSamples == 0)
        {
            return [];
        }

        var samples = new float[requestedSamples];
        var offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(samples, offset, samples.Length - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        if (offset == samples.Length)
        {
            return samples;
        }

        Array.Resize(ref samples, offset);
        return samples;
    }

    private static ReazonSpeechModelFiles ResolveModelFiles(TranscriptionModelInstallation installation)
    {
        var encoder = FindFile(installation.Files, "encoder-", ".onnx");
        var decoder = FindFile(installation.Files, "decoder-", ".onnx");
        var joiner = FindFile(installation.Files, "joiner-", ".onnx");
        var tokens = installation.Files.SingleOrDefault(path =>
            string.Equals(Path.GetFileName(path), "tokens.txt", StringComparison.OrdinalIgnoreCase));

        if (encoder is null || decoder is null || joiner is null || tokens is null)
        {
            throw new InvalidDataException("ReazonSpeechモデルを構成するencoder/decoder/joiner/tokensを解決できませんでした。");
        }

        return new ReazonSpeechModelFiles(encoder, decoder, joiner, tokens);
    }

    private static string? FindFile(IReadOnlyList<string> files, string prefix, string suffix)
        => files.SingleOrDefault(path =>
            Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static TranscriptionDocument BuildDocument(
        TranscriptionJobRequest request,
        TranscriptionModelDefinition definition,
        IReadOnlyList<TranscribedSegment> segments,
        DateTimeOffset createdAt)
    {
        return new TranscriptionDocument
        {
            Source = new TranscriptionSource(Path.GetFileName(request.AudioFilePath)),
            Transcription = new TranscriptionIdentity
            {
                Engine = request.EngineId.Value,
                Model = request.ModelId.Value,
                ModelVersion = definition.ArtifactVersion,
                ModelRevision = definition.Revision,
                Options = new Dictionary<string, string?>
                {
                    ["executionMode"] = "cpu",
                    ["language"] = "ja",
                    ["decodingMethod"] = "greedy_search"
                }
            },
            Runtime = new TranscriptionRuntime
            {
                Requested = "cpu",
                Actual = "cpu"
            },
            CreatedAt = createdAt,
            Segments = segments.Select(segment => new TranscriptionDocumentSegment
            {
                Start = segment.Start.TotalSeconds,
                End = segment.End.TotalSeconds,
                Speaker = segment.SpeakerLabel,
                Text = segment.Text
            }).ToArray()
        };
    }

    private static string BuildDocumentPath(
        string audioFilePath,
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId)
    {
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(audioFilePath);
        return Path.Combine(directory, $"{fileName}-{engineId.Value}-{modelId.Value}.json");
    }

    private static TranscriptionJobResult Fail(string message, DateTimeOffset started)
        => new(false, message, Array.Empty<string>(), started, DateTimeOffset.Now);

    private sealed record ReazonSpeechModelFiles(
        string Encoder,
        string Decoder,
        string Joiner,
        string Tokens);
}
