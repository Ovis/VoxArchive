using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engineのraw ASR traceとCommon canonical結果が詳細診断JSONで対応付くことを確認する
/// </summary>
public sealed class TranscriptionAsrDiagnosticTests
{
    [Test]
    public async Task TranscribeAsync_AsrDiagnostics_PreservesRawTextCanonicalTextDiscardAndTimestampTrace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-asr-diagnostic-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            var timestampTrace = JsonSerializer.SerializeToElement(new
            {
                tokens = new[]
                {
                    new { tokenIndex = 0, rawSeconds = 1.4, correctedChunkSample = 8_000, absoluteSample = 8_000 }
                }
            });

            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult(
                    [
                        new RecognizedTranscriptionSegment(
                            TimeSpan.Zero,
                            TimeSpan.FromMilliseconds(100),
                            "  raw text  ",
                            RecognitionChunkId: 0)
                    ],
                    Diagnostics: new TranscriptionEngineDiagnosticTrace(
                        [],
                        AsrResults:
                        [
                            new AsrResultDiagnosticTrace(0, "  raw text  ", timestampTrace),
                            new AsrResultDiagnosticTrace(1, " \t ", null, true, "whitespace-only")
                        ]))),
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
                    "asr-diagnostic-test"),
                DiagnosticsEnabled: true);

            await context.Orchestrator.TranscribeAsync(request);

            var path = Directory.EnumerateFiles(
                diagnosticDirectory,
                "*.transcription-diagnostic*.json").Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var results = json.RootElement.GetProperty("asrResults");
            var recognized = results[0];
            var discarded = results[1];

            Assert.Multiple(() =>
            {
                Assert.That(results.GetArrayLength(), Is.EqualTo(2));
                Assert.That(recognized.GetProperty("recognitionChunkId").GetInt32(), Is.Zero);
                Assert.That(recognized.GetProperty("rawText").GetString(), Is.EqualTo("  raw text  "));
                Assert.That(recognized.GetProperty("canonicalText").GetString(), Is.EqualTo("raw text"));
                Assert.That(recognized.GetProperty("discarded").GetBoolean(), Is.False);
                Assert.That(recognized.GetProperty("timestampTrace").GetProperty("tokens")[0]
                    .GetProperty("correctedChunkSample").GetInt64(), Is.EqualTo(8_000));

                Assert.That(discarded.GetProperty("recognitionChunkId").GetInt32(), Is.EqualTo(1));
                Assert.That(discarded.GetProperty("rawText").GetString(), Is.EqualTo(" \t "));
                Assert.That(discarded.GetProperty("canonicalText").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(discarded.GetProperty("discarded").GetBoolean(), Is.True);
                Assert.That(discarded.GetProperty("discardReason").GetString(), Is.EqualTo("whitespace-only"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
