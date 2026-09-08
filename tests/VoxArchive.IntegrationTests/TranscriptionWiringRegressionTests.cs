using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Runtime;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;
using VoxArchive.Transcription.SileroVad;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// PR #27で追加した実装がProductionのDI・Admission・排他経路へ実際に接続されていることを確認する
/// </summary>
public sealed class TranscriptionWiringRegressionTests
{
    private static readonly TranscriptionEngineId EngineId = new("cached-readiness-test");
    private static readonly TranscriptionModelId ModelId = new("model-a");
    private static readonly TranscriptionModelKey ModelKey = new(EngineId, ModelId);

    [Test]
    public void RuntimeComposition_ResolvesSelectedVadAndConcreteEngineChunkers()
    {
        var services = new ServiceCollection();

        // IntegrationTestsへLogging DI拡張パッケージを追加するだけのテスト依存を増やさない。
        // Production compositionの解決に必要なILogger<T>だけNullLogger<T>で満たし、実サービス登録自体はAddVoxArchiveTranscriptionをそのまま検証する。
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVoxArchiveTranscription();

        using var provider = services.BuildServiceProvider();

        Assert.Multiple(() =>
        {
            // 共通VADが旧VolumeBased実装の直接登録へ戻ると、VAD方式選択とSilero fallbackが本番で呼ばれなくなる。
            Assert.That(provider.GetRequiredService<ISpeechRegionDetector>(), Is.TypeOf<SileroPreferredSpeechRegionDetector>());

            // ChunkerはEngine固有の具象責務として登録し、削除した共通Interfaceを経由しない。
            Assert.That(provider.GetRequiredService<WhisperRecognitionChunker>(), Is.Not.Null);
            Assert.That(provider.GetRequiredService<ReazonSpeechRecognitionChunker>(), Is.Not.Null);
        });
    }

    [Test]
    public async Task Admission_VolumeBasedSetting_ReachesProductionVadSelectorSnapshot()
    {
        var registration = new TranscriptionEngineRegistration(
            new FakeEngine(),
            new FakeSettingsProvider());
        var registry = new TranscriptionEngineRegistry([registration]);
        var usageTracker = new TranscriptionModelUsageTracker();
        var service = new TranscriptionJobAdmissionService(
            registry,
            new TranscriptionModelManager(registry, usageTracker),
            usageTracker);
        var options = new RecordingOptions
        {
            Transcription = new TranscriptionSettings
            {
                Enabled = true,
                DefaultEngine = EngineId.Value,
                PreferredLanguage = "ja",
                SileroVad = new SileroVadSettings
                {
                    Mode = SpeechRegionDetectorMode.VolumeBased,
                    Threshold = 0.61d
                },
                Engines = new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    [EngineId.Value] = new()
                    {
                        SchemaVersion = 1,
                        Settings = JsonSerializer.SerializeToElement(new { })
                    }
                }
            }
        };

        var result = await service.AdmitAsync(
            "recording.flac",
            options,
            TranscriptionTrigger.Manual);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Job, Is.Not.Null);
        using var job = result.Job!;
        var snapshot = job.Request.SpeechRegionDetectorSettings;

        Assert.Multiple(() =>
        {
            // Domain設定がAdmissionでsnapshot化され、そのsnapshotを本番selectorがVolumeBasedとして解釈できることまで1本で確認する。
            Assert.That(SileroPreferredSpeechRegionDetector.UseVolumeBasedDetector(snapshot), Is.True);
            Assert.That(snapshot.Settings.GetProperty(nameof(SileroVadSettings.Threshold)).GetDouble(), Is.EqualTo(0.61d));
        });
    }

    [Test]
    public void SileroInitialStateValidation_WhileTranscriptionReserved_DoesNotNativeLoad()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            File.WriteAllBytes(modelPath, [1, 2, 3]);

            var usageTracker = new TranscriptionModelUsageTracker();
            using var reservation = usageTracker.Acquire(ModelKey);
            var validationCount = 0;
            using var client = new HttpClient();
            var manager = new SileroVadModelManager(
                new ManagedModelFileTransaction(client),
                usageTracker,
                modelPath,
                _ => validationCount++);

            Assert.Multiple(() =>
            {
                Assert.That(() => manager.GetState(), Throws.TypeOf<InvalidOperationException>());
                Assert.That(validationCount, Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void NativeReadinessCacheMiss_WhileTranscriptionReserved_IsRejectedBeforeProviderLoad()
    {
        var provider = new CachedReadinessModelProvider(cached: false);
        var (manager, usageTracker) = CreateModelManager(provider);
        using var reservation = usageTracker.Acquire(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(() => manager.IsReady(ModelKey), Throws.TypeOf<InvalidOperationException>());
            Assert.That(provider.IsReadyCallCount, Is.Zero);
        });
    }

    [Test]
    public void NativeReadinessCacheHit_WhileTranscriptionReserved_DoesNotBlockSecondAdmissionCheck()
    {
        var provider = new CachedReadinessModelProvider(cached: true, cachedValue: true);
        var (manager, usageTracker) = CreateModelManager(provider);
        using var reservation = usageTracker.Acquire(ModelKey);

        var ready = manager.IsReady(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(ready, Is.True);
            Assert.That(provider.IsReadyCallCount, Is.Zero);
        });
    }

    [Test]
    public void NativeReadinessCacheMiss_WithoutTranscription_LoadsOnceAndThenUsesCache()
    {
        var provider = new CachedReadinessModelProvider(cached: false, nativeResult: true);
        var (manager, _) = CreateModelManager(provider);

        var first = manager.IsReady(ModelKey);
        var second = manager.IsReady(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(provider.IsReadyCallCount, Is.EqualTo(1));
        });
    }

    private static (TranscriptionModelManager Manager, TranscriptionModelUsageTracker UsageTracker) CreateModelManager(
        CachedReadinessModelProvider provider)
    {
        var registration = new TranscriptionEngineRegistration(
            new FakeEngine(),
            new FakeSettingsProvider(),
            provider,
            new FakeModelRequirementResolver());
        var registry = new TranscriptionEngineRegistry([registration]);
        var usageTracker = new TranscriptionModelUsageTracker();
        return (new TranscriptionModelManager(registry, usageTracker), usageTracker);
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record FakeOptions : ITranscriptionEngineOptions;

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class FakeSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new FakeOptions();
        public JsonElement Serialize(ITranscriptionEngineOptions options) => JsonSerializer.SerializeToElement(new { });
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class FakeModelRequirementResolver : ITranscriptionModelRequirementResolver
    {
        public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options) => ModelId;
        public ITranscriptionEngineOptions SelectModel(ITranscriptionEngineOptions options, TranscriptionModelId modelId) => options;
        public ITranscriptionEngineOptions BindInstallation(ITranscriptionEngineOptions options, TranscriptionModelInstallation installation) => options;
    }

    private sealed class CachedReadinessModelProvider(
        bool cached,
        bool cachedValue = false,
        bool nativeResult = false) : ITranscriptionModelProvider, ITranscriptionModelReadinessCache
    {
        private bool _hasCache = cached;
        private bool _cachedValue = cachedValue;

        public int IsReadyCallCount { get; private set; }
        public TranscriptionEngineId EngineId => TranscriptionWiringRegressionTests.EngineId;

        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
            => [new(ModelId, "Model A")];

        public bool TryGetCachedReadiness(TranscriptionModelId modelId, out bool isReady)
        {
            isReady = _cachedValue;
            return _hasCache;
        }

        public bool IsReady(TranscriptionModelId modelId)
        {
            IsReadyCallCount++;
            _hasCache = true;
            _cachedValue = nativeResult;
            return nativeResult;
        }

        public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
            => new(_cachedValue ? TranscriptionModelPackageState.Installed : TranscriptionModelPackageState.Missing, level);

        public Task<TranscriptionModelInstallation> InstallAsync(
            TranscriptionModelId modelId,
            bool force,
            IProgress<TranscriptionModelTransferProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GetInstallation(modelId));

        public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
            => new(EngineId, modelId, ["model-a.bin"]);
    }
}
