using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Application;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// QueueとOrchestratorのIntegration Testで使用するEngine、モデル、録音ファイルを組み立てる
/// </summary>
internal static class TranscriptionPipelineTestFixture
{
    internal static readonly TranscriptionEngineId EngineId = new("test-engine");
    internal static readonly TranscriptionModelId ModelId = new("test-model");

    /// <summary>
    /// Engineの実行動作を差し替えられるテスト用pipelineを生成する
    /// </summary>
    /// <param name="transcribeAsync">Engineの認識動作</param>
    /// <param name="diagnosticDirectory">詳細診断JSONの保存先。未指定時はテスト専用一時ディレクトリを使用する</param>
    internal static PipelineContext CreatePipeline(
        Func<TranscriptionEngineRequest, CancellationToken, Task<TranscriptionEngineResult>> transcribeAsync,
        string? diagnosticDirectory = null)
    {
        var engine = new ControllableEngine(transcribeAsync);
        var settingsProvider = new TestSettingsProvider();
        var modelProvider = new ReadyModelProvider();
        var modelResolver = new TestModelRequirementResolver();
        var registration = new TranscriptionEngineRegistration(
            engine,
            settingsProvider,
            modelProvider,
            modelResolver,
            null,
            null,
            null,
            null);
        var registry = new TranscriptionEngineRegistry([registration]);
        var usageTracker = new TranscriptionModelUsageTracker();
        var modelManager = new TranscriptionModelManager(registry, usageTracker);
        diagnosticDirectory ??= Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", "diagnostics", Guid.NewGuid().ToString("N"));
        var orchestrator = new TranscriptionOrchestrator(
            registry,
            new TranscriptionAudioPreparationService(),
            new FullAudioSpeechRegionDetector(),
            new TranscriptionEngineResultValidator(),
            new TranscriptionSpeakerLabelService(),
            new TranscriptionArtifactService(new TranscriptionDocumentStore(), new TranscriptionExportService()),
            new TranscriptionDiagnosticWriter(
                NullLogger<TranscriptionDiagnosticWriter>.Instance,
                diagnosticDirectory),
            NullLogger<TranscriptionOrchestrator>.Instance);
        var admission = new TranscriptionJobAdmissionService(registry, modelManager, usageTracker);
        var queue = new TranscriptionJobQueue(admission, orchestrator, NullLogger<TranscriptionJobQueue>.Instance);

        return new PipelineContext(registry, usageTracker, modelManager, orchestrator, queue);
    }

    /// <summary>
    /// Admissionが読み込めるEngine非依存設定を生成する
    /// </summary>
    internal static RecordingOptions CreateOptions(
        TranscriptionPriority manualPriority = TranscriptionPriority.Normal,
        TranscriptionPriority autoPriority = TranscriptionPriority.Low)
        => new()
        {
            Transcription = new TranscriptionSettings
            {
                Enabled = true,
                DefaultEngine = EngineId.Value,
                PreferredLanguage = "ja",
                OutputFormats = TranscriptionOutputFormats.None,
                ManualPriority = manualPriority,
                AutoPriority = autoPriority,
                Engines = new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    [EngineId.Value] = new()
                    {
                        SchemaVersion = 1,
                        Settings = JsonSerializer.SerializeToElement(new { value = "test" })
                    }
                }
            }
        };

    /// <summary>
    /// Audio Preparationまで含めたpipelineを通せる短いmono WAVを作成する
    /// </summary>
    internal static string CreateWaveFile(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        const int sampleRate = 16_000;
        const int sampleCount = 4_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataSize = sampleCount * channels * (bitsPerSample / 8);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        for (var i = 0; i < sampleCount; i++)
        {
            writer.Write((short)0);
        }

        return path;
    }

    internal sealed record PipelineContext(
        TranscriptionEngineRegistry Registry,
        TranscriptionModelUsageTracker UsageTracker,
        TranscriptionModelManager ModelManager,
        TranscriptionOrchestrator Orchestrator,
        TranscriptionJobQueue Queue) : IDisposable
    {
        public void Dispose() => Queue.Dispose();
    }

    private sealed record TestOptions(string? InstallationPath = null) : ITranscriptionEngineOptions;

    private sealed class ControllableEngine(
        Func<TranscriptionEngineRequest, CancellationToken, Task<TranscriptionEngineResult>> transcribeAsync)
        : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => transcribeAsync(request, cancellationToken);
    }

    /// <summary>
    /// Queue/Admissionのテスト目的ではVAD精度を検証しないため、Prepared Audio全体を1発話として返す
    /// </summary>
    private sealed class FullAudioSpeechRegionDetector : IDiagnosticSpeechRegionDetector
    {
        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateRegions(audio, cancellationToken));

        public Task<SpeechRegionDetectionDiagnosticResult> DetectWithDiagnosticsAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken = default)
        {
            var regions = CreateRegions(audio, cancellationToken);
            return Task.FromResult(new SpeechRegionDetectionDiagnosticResult(
                regions,
                new SpeechRegionDetectionDiagnosticTrace(
                    nameof(FullAudioSpeechRegionDetector),
                    [],
                    FallbackUsed: false,
                    FallbackReason: null,
                    EffectiveSettings: JsonSerializer.SerializeToElement(new { mode = "full-audio-test" }))));
        }

        private static IReadOnlyList<SpeechRegion> CreateRegions(
            IPreparedTranscriptionAudio audio,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endSample = audio.SampleCount;
            return endSample == 0
                ? []
                : [new SpeechRegion(0, 0, endSample, [new AudioSampleRange(0, endSample)], [0])];
        }
    }

    private sealed class TestSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new TestOptions();
        public JsonElement Serialize(ITranscriptionEngineOptions options) => JsonSerializer.SerializeToElement(new { value = "test" });
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class ReadyModelProvider : ITranscriptionModelProvider
    {
        public TranscriptionEngineId EngineId => TranscriptionPipelineTestFixture.EngineId;

        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
            => [new(ModelId, "テストモデル")];

        public bool IsReady(TranscriptionModelId modelId) => modelId == ModelId;

        public TranscriptionModelInspection Inspect(
            TranscriptionModelId modelId,
            TranscriptionModelInspectionLevel level)
            => new(TranscriptionModelPackageState.Installed, level);

        public Task<TranscriptionModelInstallation> InstallAsync(
            TranscriptionModelId modelId,
            bool force,
            IProgress<TranscriptionModelTransferProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GetInstallation(modelId));

        public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
            => new(EngineId, modelId, ["test-model.bin"]);
    }

    private sealed class TestModelRequirementResolver : ITranscriptionModelRequirementResolver
    {
        public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options) => ModelId;

        public ITranscriptionEngineOptions SelectModel(
            ITranscriptionEngineOptions options,
            TranscriptionModelId modelId)
            => options;

        public ITranscriptionEngineOptions BindInstallation(
            ITranscriptionEngineOptions options,
            TranscriptionModelInstallation installation)
            => ((TestOptions)options) with { InstallationPath = installation.PrimaryFile };
    }
}
