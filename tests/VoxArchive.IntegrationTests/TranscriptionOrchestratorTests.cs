using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine非依存Orchestratorがpipeline順序、artifact確定、詳細診断の非侵襲性を維持することを確認する
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
    public async Task TranscribeAsync_OutOfRangeTimeline_ClampsToPreparedAudioBeforePublishingArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline((_, _) =>
                Task.FromResult(new TranscriptionEngineResult(
                    [new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), " clamped ")])));

            var result = await context.Orchestrator.TranscribeAsync(CreateRequest(source));
            var document = await new TranscriptionDocumentStore().LoadAsync(result.DocumentPath);

            Assert.That(document.Segments, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(document.Segments[0].Start, Is.EqualTo(0d));
                Assert.That(document.Segments[0].End, Is.EqualTo(0.25d).Within(0.000001d));
                Assert.That(document.Segments[0].Text, Is.EqualTo("clamped"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TranscribeAsync_DiagnosticsDisabled_DoesNotCreateDiagnosticJson()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult([])),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source, diagnosticsEnabled: false));

            Assert.That(
                Directory.Exists(diagnosticDirectory)
                    ? Directory.EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json").Any()
                    : false,
                Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TranscribeAsync_DiagnosticsEnabled_WritesSourceSettingsAndTimings()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult([])),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source, diagnosticsEnabled: true));

            var path = Directory.EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json").Single();
            var jsonText = await File.ReadAllTextAsync(path);
            using var json = JsonDocument.Parse(jsonText);
            var rootElement = json.RootElement;
            var diagnosticSource = rootElement.GetProperty("source");
            var settings = rootElement.GetProperty("settings");
            var timings = rootElement.GetProperty("timings");

            Assert.Multiple(() =>
            {
                Assert.That(rootElement.GetProperty("status").GetString(), Is.EqualTo("success"));
                Assert.That(rootElement.GetProperty("failedStage").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(diagnosticSource.GetProperty("fileName").GetString(), Is.EqualTo("recording.wav"));
                Assert.That(diagnosticSource.GetProperty("fileSizeBytes").GetInt64(), Is.EqualTo(new FileInfo(source).Length));
                Assert.That(diagnosticSource.GetProperty("durationSamples").GetInt64(), Is.EqualTo(4_000));
                Assert.That(diagnosticSource.GetProperty("sampleRate").GetInt32(), Is.EqualTo(16_000));
                Assert.That(diagnosticSource.GetProperty("preparedAudioSha256").GetString(), Has.Length.EqualTo(64));
                Assert.That(settings.GetProperty("engineId").GetString(), Is.EqualTo(TranscriptionPipelineTestFixture.EngineId.Value));
                Assert.That(settings.GetProperty("modelId").GetString(), Is.EqualTo(TranscriptionPipelineTestFixture.ModelId.Value));
                Assert.That(settings.GetProperty("engineSettingsSchemaVersion").GetInt32(), Is.EqualTo(3));
                Assert.That(settings.GetProperty("preferredLanguage").GetString(), Is.EqualTo("ja"));
                Assert.That(settings.GetProperty("vadSettingsSchemaVersion").GetInt32(), Is.EqualTo(2));
                Assert.That(settings.GetProperty("speakerGainDb").GetDouble(), Is.EqualTo(1.5d));
                Assert.That(settings.GetProperty("microphoneGainDb").GetDouble(), Is.EqualTo(-2d));
                Assert.That(timings.GetProperty("overallMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
                Assert.That(timings.GetProperty("audioPreparationMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
                Assert.That(timings.GetProperty("vadMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
                Assert.That(timings.GetProperty("asrMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
                Assert.That(jsonText, Does.Not.Contain(root));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TranscribeAsync_AsrFailure_WritesPartialDiagnosticAndPreservesOriginalException()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => throw new InvalidOperationException("engine failed"),
                diagnosticDirectory);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await context.Orchestrator.TranscribeAsync(CreateRequest(source, diagnosticsEnabled: true)));

            Assert.That(exception!.Message, Is.EqualTo("engine failed"));
            var path = Directory.EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json").Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var rootElement = json.RootElement;
            var error = rootElement.GetProperty("exception");

            Assert.Multiple(() =>
            {
                Assert.That(rootElement.GetProperty("status").GetString(), Is.EqualTo("failed"));
                Assert.That(rootElement.GetProperty("failedStage").GetString(), Is.EqualTo("asr"));
                Assert.That(error.GetProperty("exceptionType").GetString(), Does.EndWith("InvalidOperationException"));
                Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("engine failed"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranscriptionOrchestrationRequest CreateRequest(string source, bool diagnosticsEnabled = false)
    {
        var request = new TranscriptionOrchestrationRequest(
            source,
            TranscriptionPipelineTestFixture.EngineId,
            new TestOptions(),
            1.5d,
            -2d,
            new TranscriptionArtifactOptions(
                TranscriptionPipelineTestFixture.ModelId,
                TranscriptionArtifactFormats.None,
                "test-model",
                new TranscriptionExecutionSnapshot(
                    3,
                    JsonSerializer.SerializeToElement(new { mode = "test" }),
                    "ja")),
            diagnosticsEnabled)
        {
            SpeechRegionDetectorSettings = new SpeechRegionDetectorSettingsSnapshot(
                2,
                JsonSerializer.SerializeToElement(new { threshold = 0.5 }))
        };
        return request;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-orchestrator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
