using System.Text.Json;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engineが返したRecognitionChunk単位のASR実測時間が詳細診断JSONへ保持されることを確認する
/// </summary>
public sealed class TranscriptionPerChunkTimingDiagnosticTests
{
    [Test]
    public async Task TranscribeAsync_DiagnosticsEnabled_WritesPerChunkAsrElapsedMilliseconds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-chunk-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult(
                    [new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(100), "test", 3)],
                    Diagnostics: new TranscriptionEngineDiagnosticTrace(
                        [new RecognitionChunkDiagnosticTrace(3, 0, 0, 1_600, "speech-region")],
                        ChunkGenerationMilliseconds: 5,
                        AsrMilliseconds: 42,
                        AsrResults:
                        [
                            new AsrResultDiagnosticTrace(
                                3,
                                "test",
                                TimestampTrace: null,
                                Discarded: false,
                                DiscardReason: null,
                                ElapsedMilliseconds: 42)
                        ]))),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source));

            var diagnosticPath = Directory
                .EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json")
                .Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(diagnosticPath));
            var chunk = json.RootElement.GetProperty("recognitionChunks").EnumerateArray().Single();
            var asr = json.RootElement.GetProperty("asrResults").EnumerateArray().Single();
            var timings = json.RootElement.GetProperty("timings");

            Assert.Multiple(() =>
            {
                Assert.That(chunk.GetProperty("recognitionChunkId").GetInt32(), Is.EqualTo(3));
                Assert.That(chunk.GetProperty("elapsedMilliseconds").GetInt64(), Is.EqualTo(42));
                Assert.That(asr.GetProperty("elapsedMilliseconds").GetInt64(), Is.EqualTo(42));
                Assert.That(timings.GetProperty("chunkGenerationMilliseconds").GetInt64(), Is.EqualTo(5));
                Assert.That(timings.GetProperty("asrMilliseconds").GetInt64(), Is.EqualTo(42));
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
                new TranscriptionExecutionSnapshot(
                    1,
                    JsonSerializer.SerializeToElement(new { mode = "test" }),
                    "ja")),
            DiagnosticsEnabled: true)
        {
            SpeechRegionDetectorSettings = new SpeechRegionDetectorSettingsSnapshot(
                1,
                JsonSerializer.SerializeToElement(new { threshold = 0.5 }))
        };

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
