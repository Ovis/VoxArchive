using System.Text.Json;
using VoxArchive.Application;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine settings編集Use CaseがEngine固有JSON構造をApplication/UIへ漏らさずcapability経由で操作することを確認する
/// </summary>
public sealed class TranscriptionEngineSettingsServiceTests
{
    private static readonly TranscriptionEngineId EngineId = new("test-engine");

    [Test]
    public void GetConfiguration_ProjectsModelAndExecutionModeThroughCapabilities()
    {
        var service = CreateService();
        var settings = CreateSettings("model-a", "auto");

        var actual = service.GetConfiguration(EngineId.Value, settings);

        Assert.Multiple(() =>
        {
            Assert.That(actual.ModelId, Is.EqualTo("model-a"));
            Assert.That(actual.ExecutionModeId, Is.EqualTo("auto"));
            Assert.That(actual.ExecutionModes.Select(x => x.Id), Is.EquivalentTo(new[] { "auto", "cpu" }));
        });
    }

    [Test]
    public void UpdateConfiguration_UsesCapabilitiesAndSerializesUpdatedOptions()
    {
        var service = CreateService();
        var settings = CreateSettings("model-a", "auto");

        var updated = service.UpdateConfiguration(
            EngineId.Value,
            settings,
            "model-b",
            "cpu");
        var configuration = service.GetConfiguration(EngineId.Value, updated);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.ModelId, Is.EqualTo("model-b"));
            Assert.That(configuration.ExecutionModeId, Is.EqualTo("cpu"));
            Assert.That(updated.Settings.GetProperty("opaqueModel").GetString(), Is.EqualTo("model-b"));
            Assert.That(updated.Settings.GetProperty("opaqueMode").GetString(), Is.EqualTo("cpu"));
        });
    }

    private static TranscriptionEngineSettingsService CreateService()
    {
        var registration = new TranscriptionEngineRegistration(
            new FakeEngine(),
            new FakeSettingsProvider(),
            new FakeModelProvider(),
            new FakeModelResolver(),
            null,
            null,
            null,
            null,
            new FakeExecutionModeCapability());
        return new TranscriptionEngineSettingsService(new TranscriptionEngineRegistry([registration]));
    }

    private static TranscriptionEngineSettings CreateSettings(string modelId, string executionMode)
        => new()
        {
            SchemaVersion = 1,
            Settings = JsonSerializer.SerializeToElement(new
            {
                opaqueModel = modelId,
                opaqueMode = executionMode
            })
        };

    private sealed record FakeOptions(string ModelId, string ExecutionMode) : ITranscriptionEngineOptions;

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);
        public Task<TranscriptionEngineResult> TranscribeAsync(TranscriptionEngineRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class FakeSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion)
            => new FakeOptions(
                settings.GetProperty("opaqueModel").GetString() ?? string.Empty,
                settings.GetProperty("opaqueMode").GetString() ?? string.Empty);

        public JsonElement Serialize(ITranscriptionEngineOptions options)
        {
            var value = GetOptions(options);
            return JsonSerializer.SerializeToElement(new
            {
                opaqueModel = value.ModelId,
                opaqueMode = value.ExecutionMode
            });
        }

        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class FakeModelProvider : ITranscriptionModelProvider
    {
        public TranscriptionEngineId EngineId => TranscriptionEngineSettingsServiceTests.EngineId;
        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels() => [];
        public bool IsReady(TranscriptionModelId modelId) => true;
        public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
            => throw new NotSupportedException();
        public Task<TranscriptionModelInstallation> InstallAsync(TranscriptionModelId modelId, bool force, IProgress<TranscriptionModelTransferProgress>? progress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
            => throw new NotSupportedException();
    }

    private sealed class FakeModelResolver : ITranscriptionModelRequirementResolver
    {
        public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options)
            => new(GetOptions(options).ModelId);

        public ITranscriptionEngineOptions SelectModel(ITranscriptionEngineOptions options, TranscriptionModelId modelId)
            => GetOptions(options) with { ModelId = modelId.Value };

        public ITranscriptionEngineOptions BindInstallation(ITranscriptionEngineOptions options, TranscriptionModelInstallation installation)
            => options;
    }

    private sealed class FakeExecutionModeCapability : ITranscriptionExecutionModeCapability
    {
        public IReadOnlyList<TranscriptionExecutionModeDescriptor> GetAvailableModes()
            => [new("auto", "自動"), new("cpu", "CPU")];

        public string GetSelectedMode(ITranscriptionEngineOptions options) => GetOptions(options).ExecutionMode;

        public ITranscriptionEngineOptions SelectMode(ITranscriptionEngineOptions options, string modeId)
            => GetOptions(options) with { ExecutionMode = modeId };
    }

    private static FakeOptions GetOptions(ITranscriptionEngineOptions options)
        => options as FakeOptions
           ?? throw new ArgumentException("テスト用Engine optionsではありません。", nameof(options));
}
