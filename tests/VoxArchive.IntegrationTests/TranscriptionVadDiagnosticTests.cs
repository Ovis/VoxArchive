using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 詳細診断JSONにCommon VADが確定したSpeechRegionのsample座標、lineage、実効設定が保存されることを確認する
/// </summary>
public sealed class TranscriptionVadDiagnosticTests
{
    [Test]
    public async Task TranscribeAsync_DiagnosticsEnabled_WritesFinalSpeechRegionLineageAndEffectiveSettings()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "recording.wav");
            var diagnosticDirectory = Path.Combine(root, "diagnostics");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(
                (_, _) => Task.FromResult(new TranscriptionEngineResult([])),
                diagnosticDirectory);

            await context.Orchestrator.TranscribeAsync(CreateRequest(source));

            var path = Directory.EnumerateFiles(diagnosticDirectory, "*.transcription-diagnostic*.json").Single();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var rootElement = json.RootElement;
            var requestedVadSettings = rootElement.GetProperty("settings").GetProperty("vadSettings");
            var vad = rootElement.GetProperty("vad");
            var effectiveVadSettings = vad.GetProperty("settings");
            var regions = vad.GetProperty("speechRegions");
            var region = regions[0];
            var core = region.GetProperty("coreRanges")[0];
            var rawIds = region.GetProperty("sourceRawSpeechRegionIds");

            Assert.Multiple(() =>
            {
                // Jobに要求された設定と、実際に選択されたdetectorの設定を別々に保持する。
                Assert.That(requestedVadSettings.GetProperty("threshold").GetDouble(), Is.EqualTo(0.5d));
                Assert.That(effectiveVadSettings.GetProperty("mode").GetString(), Is.EqualTo("full-audio-test"));
                Assert.That(vad.GetProperty("detector").GetString(), Does.Contain("FullAudioSpeechRegionDetector"));
                Assert.That(vad.GetProperty("fallbackUsed").GetBoolean(), Is.False);
                Assert.That(vad.GetProperty("fallbackReason").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(vad.GetProperty("elapsedMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
                Assert.That(regions.GetArrayLength(), Is.EqualTo(1));
                Assert.That(region.GetProperty("speechRegionId").GetInt32(), Is.EqualTo(0));
                Assert.That(region.GetProperty("startSample").GetInt64(), Is.EqualTo(0));
                Assert.That(region.GetProperty("endSample").GetInt64(), Is.EqualTo(4_000));
                Assert.That(core.GetProperty("startSample").GetInt64(), Is.EqualTo(0));
                Assert.That(core.GetProperty("endSample").GetInt64(), Is.EqualTo(4_000));
                Assert.That(rawIds.GetArrayLength(), Is.EqualTo(1));
                Assert.That(rawIds[0].GetInt32(), Is.EqualTo(0));
                Assert.That(vad.GetProperty("rawRegions").GetArrayLength(), Is.EqualTo(0));
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
            true)
        {
            SpeechRegionDetectorSettings = new SpeechRegionDetectorSettingsSnapshot(
                1,
                JsonSerializer.SerializeToElement(new { threshold = 0.5 }))
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-vad-diagnostic-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record TestOptions : ITranscriptionEngineOptions;
}
