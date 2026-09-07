using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine RegistryがEngineとcapabilityの登録整合性を検証することを確認する
/// </summary>
public sealed class TranscriptionEngineRegistryTests
{
    [Test]
    public void Constructor_RejectsDuplicateEngineIds()
    {
        var id = new TranscriptionEngineId("test");
        var registrations = new[]
        {
            CreateRegistration(id),
            CreateRegistration(id)
        };

        Assert.That(
            () => new TranscriptionEngineRegistry(registrations),
            Throws.InvalidOperationException.With.Message.Contains("重複"));
    }

    [Test]
    public void Constructor_RejectsMismatchedModelProviderId()
    {
        var engineId = new TranscriptionEngineId("engine");
        var registration = new TranscriptionEngineRegistration(
            new StubEngine(engineId),
            new StubSettingsProvider(),
            new StubModelProvider(new TranscriptionEngineId("other")));

        Assert.That(
            () => new TranscriptionEngineRegistry([registration]),
            Throws.InvalidOperationException.With.Message.Contains("一致しません"));
    }

    private static TranscriptionEngineRegistration CreateRegistration(TranscriptionEngineId id)
        => new(new StubEngine(id), new StubSettingsProvider());

    private sealed class StubEngine(TranscriptionEngineId id) : ITranscriptionEngine
    {
        public TranscriptionEngineId Id { get; } = id;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(TranscriptionEngineRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class StubSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new StubOptions();
        public JsonElement Serialize(ITranscriptionEngineOptions options) => JsonSerializer.SerializeToElement(new { });
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class StubOptions : ITranscriptionEngineOptions;

    private sealed class StubModelProvider(TranscriptionEngineId engineId) : ITranscriptionModelProvider
    {
        public TranscriptionEngineId EngineId { get; } = engineId;

        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels() => [];

        public bool IsReady(TranscriptionModelId modelId) => false;

        public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
            => new(TranscriptionModelPackageState.Missing, level);

        public Task<TranscriptionModelInstallation> InstallAsync(
            TranscriptionModelId modelId,
            bool force,
            IProgress<TranscriptionModelTransferProgress>? progress,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
            => throw new InvalidOperationException();
    }
}
