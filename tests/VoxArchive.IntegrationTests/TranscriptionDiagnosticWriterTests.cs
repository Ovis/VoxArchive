using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Transcription;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 詳細文字起こし診断writerがファイル命名と非致命的な保存失敗を仕様どおり扱うことを確認する
/// </summary>
public sealed class TranscriptionDiagnosticWriterTests
{
    [Test]
    public async Task TryWriteAsync_WritesSchemaWithoutAbsoluteSourcePathAndUsesCollisionSuffix()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var logsDirectory = Path.Combine(root, "logs");
            var writer = new TranscriptionDiagnosticWriter(
                NullLogger<TranscriptionDiagnosticWriter>.Instance,
                logsDirectory);
            var sourcePath = Path.Combine(root, "recordings", "meeting.wav");
            var timestamp = new DateTimeOffset(2026, 9, 8, 10, 15, 30, TimeSpan.FromHours(9));
            var document = CreateDocument("meeting.wav");

            var first = await writer.TryWriteAsync(sourcePath, timestamp, document);
            var second = await writer.TryWriteAsync(sourcePath, timestamp, document);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
                Assert.That(Path.GetFileName(first!), Is.EqualTo("meeting-20260908-101530.transcription-diagnostic.json"));
                Assert.That(Path.GetFileName(second!), Is.EqualTo("meeting-20260908-101530.transcription-diagnostic-2.json"));
                Assert.That(File.Exists(first!), Is.True);
                Assert.That(File.Exists(second!), Is.True);
            });

            var jsonText = await File.ReadAllTextAsync(first!);
            using var json = JsonDocument.Parse(jsonText);
            var rootElement = json.RootElement;
            var source = rootElement.GetProperty("source");

            Assert.Multiple(() =>
            {
                Assert.That(rootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
                Assert.That(source.GetProperty("fileName").GetString(), Is.EqualTo("meeting.wav"));
                Assert.That(source.TryGetProperty("path", out _), Is.False);
                Assert.That(source.TryGetProperty("absolutePath", out _), Is.False);
                Assert.That(jsonText, Does.Not.Contain(sourcePath));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryWriteAsync_WhenLogsDirectoryCannotBeCreated_ReturnsNullInsteadOfFailingJob()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var blockingFile = Path.Combine(root, "not-a-directory");
            await File.WriteAllTextAsync(blockingFile, "block");
            var impossibleLogsDirectory = Path.Combine(blockingFile, "logs");
            var writer = new TranscriptionDiagnosticWriter(
                NullLogger<TranscriptionDiagnosticWriter>.Instance,
                impossibleLogsDirectory);

            var result = await writer.TryWriteAsync(
                Path.Combine(root, "recording.wav"),
                DateTimeOffset.Now,
                CreateDocument("recording.wav"));

            Assert.That(result, Is.Null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TryWriteAsync_WhenCancelled_PropagatesCancellation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var writer = new TranscriptionDiagnosticWriter(
                NullLogger<TranscriptionDiagnosticWriter>.Instance,
                Path.Combine(root, "logs"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.That(
                async () => await writer.TryWriteAsync(
                    Path.Combine(root, "recording.wav"),
                    DateTimeOffset.Now,
                    CreateDocument("recording.wav"),
                    cancellation.Token),
                Throws.TypeOf<OperationCanceledException>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranscriptionDiagnosticDocument CreateDocument(string fileName)
        => new()
        {
            ApplicationVersion = "test",
            Source = new TranscriptionDiagnosticSource(
                fileName,
                1234,
                16000,
                16000,
                "ABCDEF")
        };

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
