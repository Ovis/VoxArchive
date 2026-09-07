using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine非依存Orchestratorがpipeline順序とartifact確定責務を維持することを確認する
/// </summary>
public sealed class TranscriptionOrchestratorTests
{
    [Test]
    public async Task TranscribeAsync_ValidEngineResult_WritesCanonicalAndPreservesMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline((_, _) =>
                Task.FromResult(new TranscriptionEngineResult(
                    [new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(100), "hello")],
                    new Dictionary<string, object?> { ["backend"] = "test-runtime" })));

            var result = await context.Orchestrator.TranscribeAsync(
                CreateRequest(source),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(result.DocumentPath), Is.True);
                Assert.That(result.GeneratedFiles, Does.Contain(result.DocumentPath));
                Assert.That(result.EngineMetadata?["backend"], Is.EqualTo("test-runtime"));
            });

            var document = await new TranscriptionDocumentStore().LoadAsync(result.DocumentPath);
            Assert.Multiple(() =>
            {
                Assert.That(document.EngineId, Is.EqualTo(TranscriptionPipelineTestFixture.EngineId.Value));
                Assert.That(document.ModelId, Is.EqualTo(TranscriptionPipelineTestFixture.ModelId.Value));
                Assert.That(document.Segments, Has.Count.EqualTo(1));
                Assert.That(document.Segments[0].Text, Is.EqualTo("hello"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TranscribeAsync_InvalidAbsoluteTimeline_FailsBeforePublishingArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline((_, _) =>
                Task.FromResult(new TranscriptionEngineResult(
                    [new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "invalid")])));
            var request = CreateRequest(source);
            var expectedPath = TranscriptionArtifactService.BuildDocumentPath(
                source,
                request.EngineId,
                request.ArtifactOptions.ModelId,
                request.ArtifactOptions.FileNameSuffix);

            Assert.That(
                async () => await context.Orchestrator.TranscribeAsync(request),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(File.Exists(expectedPath), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranscriptionOrchestrationRequest CreateRequest(string source)
        => new(
            source,
            TranscriptionPipelineTestFixture.EngineId,
            new TestOptions(),
            0d,
            0d,
            new TranscriptionArtifactOptions(
                TranscriptionPipelineTestFixture.ModelId,
                TranscriptionArtifactFormats.None,
                "test-model",
                null),
            false);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-orchestrator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
