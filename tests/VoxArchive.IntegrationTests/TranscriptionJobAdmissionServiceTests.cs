using System.Text.Json;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Job AdmissionがEngine固有知識を持たず、enqueue前に実行条件を確定することを確認する
/// </summary>
public sealed class TranscriptionJobAdmissionServiceTests
{
    private static readonly TranscriptionEngineId EngineId = new("test-engine");
    private static readonly TranscriptionModelId ModelId = new("test-model");

    [Test]
    public async Task AdmitAsync_UnsupportedPreferredLanguage_IsRejectedBeforeQueueing()
    {
        var registration = CreateRegistration(
            languageCapability: new FakeLanguageCapability(supported: false));
        var service = CreateService(registration);

        var result = await service.AdmitAsync(
            "recording.flac",
            CreateOptions(preferredLanguage: "en"),
            TranscriptionTrigger.Manual);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Job, Is.Null);
            Assert.That(result.Message, Does.Contain("サポートしていません"));
        });
    }

    [Test]
    public async Task AdmitAsync_InvalidEngineSettings_IsRejectedBeforeExecutionValidation()
    {
        var validator = new FakeExecutionValidator([]);
        var registration = CreateRegistration(
            settingsProvider: new FakeSettingsProvider([new("test.invalid", "設定が不正です。")]),
            executionValidator: validator);
        var service = CreateService(registration);

        var result = await service.AdmitAsync(
            "recording.flac",
            CreateOptions(),
            TranscriptionTrigger.Manual);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Message, Does.Contain("設定が不正です。"));
            Assert.That(validator.CallCount, Is.Zero);
        });
    }

    [Test]
    public async Task AdmitAsync_ExplicitExecutionUnavailable_IsRejectedBeforeQueueing()
    {
        var registration = CreateRegistration(
            executionValidator: new FakeExecutionValidator([
                new("test.backend.unavailable", "指定されたBackendを利用できません。")
            ]));
        var service = CreateService(registration);

        var result = await service.AdmitAsync(
            "recording.flac",
            CreateOptions(),
            TranscriptionTrigger.Manual);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Job, Is.Null);
            Assert.That(result.Message, Does.Contain("Backendを利用できません"));
        });
    }

    [Test]
    public async Task AdmitAsync_MissingManagedModel_ManualRequiresModel_AutoSkips()
    {
        var provider = new FakeModelProvider(isReady: false);
        var registration = CreateRegistration(
            modelProvider: provider,
            modelRequirementResolver: new FakeModelRequirementResolver());
        var service = CreateService(registration);
        var options = CreateOptions();

        var manual = await service.AdmitAsync(
            "recording.flac",
            options,
            TranscriptionTrigger.Manual);
        var automatic = await service.AdmitAsync(
            "recording.flac",
            options,
            TranscriptionTrigger.AutoAfterRecord);

        Assert.Multiple(() =>
        {
            Assert.That(manual.Succeeded, Is.False);
            Assert.That(manual.MissingModel?.ModelId, Is.EqualTo(ModelId.Value));
            Assert.That(automatic.Succeeded, Is.False);
            Assert.That(automatic.MissingModel, Is.Null);
            Assert.That(automatic.Message, Does.Contain("スキップ"));
        });
    }

    [Test]
    public async Task AdmitAsync_ReadyJob_CapturesCommonExecutionSnapshotAndReservation()
    {
        var provider = new FakeModelProvider(isReady: true);
        var usageTracker = new TranscriptionModelUsageTracker();
        var registration = CreateRegistration(
            modelProvider: provider,
            modelRequirementResolver: new FakeModelRequirementResolver(),
            artifactNamingCapability: new FakeArtifactNamingCapability());
        var registry = new TranscriptionEngineRegistry([registration]);
        var service = new TranscriptionJobAdmissionService(
            registry,
            new TranscriptionModelManager(registry, usageTracker),
            usageTracker);
        var options = CreateOptions(diagnosticsEnabled: true);

        var result = await service.AdmitAsync(
            "recording.flac",
            options,
            TranscriptionTrigger.Manual);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Job, Is.Not.Null);
        using var job = result.Job!;

        var key = new TranscriptionModelKey(EngineId, ModelId);
        var executionSnapshot = job.Request.ArtifactOptions.ExecutionSnapshot;
        var vadSnapshot = job.Request.SpeechRegionDetectorSettings;
        Assert.Multiple(() =>
        {
            Assert.That(job.Descriptor.EngineId, Is.EqualTo(EngineId));
            Assert.That(job.Descriptor.ModelId, Is.EqualTo(ModelId));
            Assert.That(job.Descriptor.DiagnosticsEnabled, Is.True);
            Assert.That(job.Request.DiagnosticsEnabled, Is.True);
            Assert.That(job.Request.ArtifactOptions.FileNameSuffix, Is.EqualTo("legacy-test-model"));
            Assert.That(executionSnapshot, Is.Not.Null);
            Assert.That(executionSnapshot!.EngineSettingsSchemaVersion, Is.EqualTo(1));
            Assert.That(executionSnapshot.PreferredLanguage, Is.EqualTo("ja"));
            Assert.That(executionSnapshot.EngineSettings.GetProperty("value").GetString(), Is.EqualTo("test"));
            Assert.That(vadSnapshot.SchemaVersion, Is.EqualTo(1));
            Assert.That(vadSnapshot.Settings.GetProperty(nameof(SileroVadSettings.Threshold)).GetDouble(), Is.EqualTo(0.73d));
            Assert.That(vadSnapshot.Settings.GetProperty(nameof(SileroVadSettings.MinimumSpeechDurationMilliseconds)).GetInt32(), Is.EqualTo(180));
            Assert.That(vadSnapshot.Settings.GetProperty(nameof(SileroVadSettings.MinimumSilenceDurationMilliseconds)).GetInt32(), Is.EqualTo(760));
            Assert.That(vadSnapshot.Settings.GetProperty(nameof(SileroVadSettings.PrePaddingMilliseconds)).GetInt32(), Is.EqualTo(420));
            Assert.That(vadSnapshot.Settings.GetProperty(nameof(SileroVadSettings.PostPaddingMilliseconds)).GetInt32(), Is.EqualTo(310));
            Assert.That(usageTracker.IsInUse(key), Is.True);
        });

        job.Dispose();
        Assert.That(usageTracker.IsInUse(key), Is.False);
    }

    private static TranscriptionJobAdmissionService CreateService(TranscriptionEngineRegistration registration)
    {
        var usageTracker = new TranscriptionModelUsageTracker();
        var registry = new TranscriptionEngineRegistry([registration]);
        return new TranscriptionJobAdmissionService(
            registry,
            new TranscriptionModelManager(registry, usageTracker),
            usageTracker);
    }

    private static TranscriptionEngineRegistration CreateRegistration(
        ITranscriptionEngineSettingsProvider? settingsProvider = null,
        ITranscriptionModelProvider? modelProvider = null,
        ITranscriptionModelRequirementResolver? modelRequirementResolver = null,
        ITranscriptionLanguageCapability? languageCapability = null,
        ITranscriptionEngineExecutionValidator? executionValidator = null,
        ITranscriptionArtifactNamingCapability? artifactNamingCapability = null)
        => new(
            new FakeEngine(),
            settingsProvider ?? new FakeSettingsProvider([]),
            modelProvider,
            modelRequirementResolver,
            null,
            languageCapability,
            executionValidator,
            artifactNamingCapability);

    private static RecordingOptions CreateOptions(
        string preferredLanguage = "ja",
        bool diagnosticsEnabled = false)
        => new()
        {
            Transcription = new TranscriptionSettings
            {
                Enabled = true,
                DefaultEngine = EngineId.Value,
                PreferredLanguage = preferredLanguage,
                DiagnosticsLogEnabled = diagnosticsEnabled,
                SileroVad = new SileroVadSettings
                {
                    Threshold = 0.73d,
                    MinimumSpeechDurationMilliseconds = 180,
                    MinimumSilenceDurationMilliseconds = 760,
                    PrePaddingMilliseconds = 420,
                    PostPaddingMilliseconds = 310
                },
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

    private sealed record FakeOptions(string Language = "", string? InstallationPath = null) : ITranscriptionEngineOptions;

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class FakeSettingsProvider(IReadOnlyList<TranscriptionValidationError> errors)
        : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new FakeOptions();
        public JsonElement Serialize(ITranscriptionEngineOptions options) => JsonSerializer.SerializeToElement(new { });
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => errors;
    }

    private sealed class FakeLanguageCapability(bool supported) : ITranscriptionLanguageCapability
    {
        public bool Supports(string? preferredLanguage) => supported;

        public ITranscriptionEngineOptions Resolve(ITranscriptionEngineOptions options, string? preferredLanguage)
            => ((FakeOptions)options) with { Language = preferredLanguage ?? string.Empty };
    }

    private sealed class FakeExecutionValidator(IReadOnlyList<TranscriptionValidationError> errors)
        : ITranscriptionEngineExecutionValidator
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<TranscriptionValidationError>> ValidateAsync(
            ITranscriptionEngineOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(errors);
        }
    }

    private sealed class FakeModelProvider(bool isReady) : ITranscriptionModelProvider
    {
        public TranscriptionEngineId EngineId => TranscriptionJobAdmissionServiceTests.EngineId;

        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
            => [new(ModelId, "テストモデル")];

        public bool IsReady(TranscriptionModelId modelId) => isReady;

        public TranscriptionModelInspection Inspect(
            TranscriptionModelId modelId,
            TranscriptionModelInspectionLevel level)
            => new(isReady ? TranscriptionModelPackageState.Installed : TranscriptionModelPackageState.Missing, level);

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

    private sealed class FakeModelRequirementResolver : ITranscriptionModelRequirementResolver
    {
        public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options) => ModelId;

        public ITranscriptionEngineOptions SelectModel(
            ITranscriptionEngineOptions options,
            TranscriptionModelId modelId)
            => options;

        public ITranscriptionEngineOptions BindInstallation(
            ITranscriptionEngineOptions options,
            TranscriptionModelInstallation installation)
            => ((FakeOptions)options) with { InstallationPath = installation.PrimaryFile };
    }

    private sealed class FakeArtifactNamingCapability : ITranscriptionArtifactNamingCapability
    {
        public string BuildFileNameSuffix(TranscriptionModelId? modelId)
            => $"legacy-{modelId?.Value ?? "none"}";
    }
}
