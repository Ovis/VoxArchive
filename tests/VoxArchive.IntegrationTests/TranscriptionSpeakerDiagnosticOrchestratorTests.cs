using System.Text.Json;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Orchestratorが話者判定結果を詳細診断JSONへ接続することを確認する
/// </summary>
public sealed class TranscriptionSpeakerDiagnosticOrchestratorTests
{
    [Test]
    public async Task TranscribeAsync_DiagnosticsEnabled_WritesSpeakerResultForCanonicalSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-speaker-orchestrator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult(
                    [new RecognizedTranscriptionSegment(
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(100),
                        "test",
                        RecognitionChunkId: 7)])),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source));

            var diagnosticPath = Directory
                .EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json")
                .Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(diagnosticPath));
            var speaker = json.RootElement.GetProperty("speakerResults").EnumerateArray().Single();

            Assert.Multiple(() =>
            {
                Assert.That(speaker.GetProperty("recognitionChunkId").GetInt32(), Is.EqualTo(7));
                // fixtureはmono録音なので既存仕様では話者ラベルを付けず、存在しないCH1/CH2根拠も推測しない。
                Assert.That(speaker.GetProperty("speakerLabel").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(speaker.GetProperty("speakerChannelEnergy").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(speaker.GetProperty("microphoneChannelEnergy").ValueKind, Is.EqualTo(JsonValueKind.Null));
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
