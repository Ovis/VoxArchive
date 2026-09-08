using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine内部で分離計測したchunking時間とASR時間が診断JSONへそのまま反映されることを確認する
/// </summary>
public sealed class TranscriptionDiagnosticTimingTests
{
    [Test]
    public async Task TranscribeAsync_EngineDiagnosticTiming_WritesSeparatedChunkingAndAsrTimings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-diagnostic-timing-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult(
                    [],
                    Diagnostics: new TranscriptionEngineDiagnosticTrace([], 12, 34))),
                diagnosticDirectory);

            var request = new TranscriptionOrchestrationRequest(
                source,
                TranscriptionPipelineTestFixture.EngineId,
                new TestOptions(),
                0d,
                0d,
                new TranscriptionArtifactOptions(
                    TranscriptionPipelineTestFixture.ModelId,
                    TranscriptionArtifactFormats.None,
                    "timing-test"),
                DiagnosticsEnabled: true);

            await context.Orchestrator.TranscribeAsync(request);

            var path = Directory.EnumerateFiles(
                diagnosticDirectory,
                "*.transcription-diagnostic*.json").Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var timings = json.RootElement.GetProperty("timings");

            Assert.Multiple(() =>
            {
                Assert.That(timings.GetProperty("chunkGenerationMilliseconds").GetInt64(), Is.EqualTo(12));
                Assert.That(timings.GetProperty("asrMilliseconds").GetInt64(), Is.EqualTo(34));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
