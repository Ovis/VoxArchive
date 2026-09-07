using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Common artifact pipelineがcanonical JSONを正本として派生形式を生成することを確認する
/// </summary>
public sealed class TranscriptionArtifactServiceTests
{
    [Test]
    public async Task WriteAsync_WritesCanonicalAndAllDerivedFormatsFromCommonResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "voxarchive-artifact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "meeting.flac");
            var segment = new RecognizedTranscriptionSegment(
                TimeSpan.FromSeconds(1.25),
                TimeSpan.FromSeconds(2.75),
                "こんにちは");
            var engineResult = new TranscriptionEngineResult(
                [segment],
                new Dictionary<string, object?> { ["backend"] = "test" });
            var labeled = new[] { new LabeledTranscriptionSegment(segment, "Speaker") };
            var createdAt = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.FromHours(9));
            var executionSnapshot = new TranscriptionExecutionSnapshot(
                3,
                JsonSerializer.SerializeToElement(new { mode = "fast", modelId = "model" }),
                "ja");
            var service = new TranscriptionArtifactService(
                new TranscriptionDocumentStore(),
                new TranscriptionExportService());

            var result = await service.WriteAsync(
                source,
                new TranscriptionEngineId("test"),
                new TranscriptionArtifactOptions(
                    new TranscriptionModelId("model"),
                    TranscriptionArtifactFormats.Txt | TranscriptionArtifactFormats.Srt | TranscriptionArtifactFormats.Vtt,
                    "test-model",
                    executionSnapshot),
                engineResult,
                labeled,
                createdAt);

            var document = await new TranscriptionDocumentStore().LoadAsync(result.DocumentPath);
            Assert.Multiple(() =>
            {
                Assert.That(document.SchemaVersion, Is.EqualTo(TranscriptionDocument.CurrentSchemaVersion));
                Assert.That(document.SourceFileName, Is.EqualTo("meeting.flac"));
                Assert.That(document.EngineId, Is.EqualTo("test"));
                Assert.That(document.ModelId, Is.EqualTo("model"));
                Assert.That(document.ExecutionSnapshot, Is.Not.Null);
                Assert.That(document.ExecutionSnapshot!.EngineSettingsSchemaVersion, Is.EqualTo(3));
                Assert.That(document.ExecutionSnapshot.PreferredLanguage, Is.EqualTo("ja"));
                Assert.That(document.ExecutionSnapshot.EngineSettings.GetProperty("mode").GetString(), Is.EqualTo("fast"));
                Assert.That(document.CreatedAt, Is.EqualTo(createdAt));
                Assert.That(document.Segments, Has.Count.EqualTo(1));
                Assert.That(document.Segments[0].Start, Is.EqualTo(1.25));
                Assert.That(document.Segments[0].End, Is.EqualTo(2.75));
                Assert.That(document.Segments[0].Text, Is.EqualTo("こんにちは"));
                Assert.That(document.Segments[0].Speaker, Is.EqualTo("Speaker"));
                Assert.That(result.GeneratedFiles.Select(Path.GetExtension),
                    Is.EquivalentTo(new[] { ".json", ".txt", ".srt", ".vtt" }));
            });

            var basePath = Path.Combine(root, "meeting-test-model");
            Assert.That(await File.ReadAllTextAsync(basePath + ".txt"), Is.EqualTo("[Speaker] こんにちは"));
            Assert.That(await File.ReadAllTextAsync(basePath + ".srt"), Does.Contain("00:00:01,250 --> 00:00:02,750"));
            Assert.That(await File.ReadAllTextAsync(basePath + ".vtt"), Does.StartWith("WEBVTT"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_OldCanonicalSchema_IsRejectedWithoutMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "voxarchive-artifact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "old.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":999,\"sourceFileName\":\"meeting.flac\",\"engineId\":\"test\",\"segments\":[]}");

            Assert.That(
                async () => await new TranscriptionDocumentStore().LoadAsync(path),
                Throws.TypeOf<InvalidDataException>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
