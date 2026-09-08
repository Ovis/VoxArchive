using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engineが返したRecognitionChunk診断traceがCommon診断JSONへ欠落なく保存されることを確認する
/// </summary>
public sealed class TranscriptionRecognitionDiagnosticTests
{
    [Test]
    public async Task TranscribeAsync_DiagnosticsEnabled_WritesRecognitionChunkTrace()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult(
                    [],
                    Diagnostics: new TranscriptionEngineDiagnosticTrace(
                    [
                        new RecognitionChunkDiagnosticTrace(3, 7, 100, 2_500, "forced-rms")
                    ]))),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source));

            var path = Directory.EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json").Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var chunks = json.RootElement.GetProperty("recognitionChunks");
            var chunk = chunks[0];

            Assert.Multiple(() =>
            {
                Assert.That(chunks.GetArrayLength(), Is.EqualTo(1));
                Assert.That(chunk.GetProperty("recognitionChunkId").GetInt32(), Is.EqualTo(3));
                Assert.That(chunk.GetProperty("speechRegionId").GetInt32(), Is.EqualTo(7));
                Assert.That(chunk.GetProperty("startSample").GetInt64(), Is.EqualTo(100));
                Assert.That(chunk.GetProperty("endSample").GetInt64(), Is.EqualTo(2_500));
                Assert.That(chunk.GetProperty("splitReason").GetString(), Is.EqualTo("forced-rms"));
                Assert.That(chunk.GetProperty("splitDetails").ValueKind, Is.EqualTo(JsonValueKind.Null));
            });
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
            true);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-recognition-diagnostic-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
