using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

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
        public TranscriptionAudioRequirements AudioRequirements { get; } =
            new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class StubSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new StubOptions();
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class StubOptions : ITranscriptionEngineOptions;

    private sealed class StubModelProvider(TranscriptionEngineId engineId) : ITranscriptionModelProvider
    {
        public TranscriptionEngineId EngineId { get; } = engineId;
    }
}
