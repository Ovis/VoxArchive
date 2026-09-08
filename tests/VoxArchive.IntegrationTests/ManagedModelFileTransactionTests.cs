using System.Net;
using VoxArchive.Transcription;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// load validation型モデル取得が既存正常モデルを壊さずatomic commitすることを確認する
/// </summary>
public sealed class ManagedModelFileTransactionTests
{
    [Test]
    public async Task DownloadValidateCommitAsync_ValidationFailurePreservesExistingModel()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "reazonspeech");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "working");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["model.onnx"] = [1, 2, 3]
            });
            var transaction = new ManagedModelFileTransaction(client);

            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await transaction.DownloadValidateCommitAsync(
                    [new ManagedModelDownloadFile(new Uri("https://example.invalid/model.onnx"), "model.onnx", 3)],
                    destination,
                    temporaryRoot,
                    _ => throw new InvalidDataException("load failed")));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(destination, "old.txt")), Is.EqualTo("working"));
                Assert.That(File.Exists(Path.Combine(destination, "model.onnx")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DownloadValidateCommitAsync_CommittedValidationFailureRestoresExistingModel()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "reazonspeech");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "working");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["model.onnx"] = [1, 2, 3]
            });
            var transaction = new ManagedModelFileTransaction(client);

            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await transaction.DownloadValidateCommitAsync(
                    [new ManagedModelDownloadFile(new Uri("https://example.invalid/model.onnx"), "model.onnx", 3)],
                    destination,
                    temporaryRoot,
                    _ => Task.CompletedTask,
                    validateCommittedAsync: _ => throw new InvalidDataException("official path load failed")));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(destination, "old.txt")), Is.EqualTo("working"));
                Assert.That(File.Exists(Path.Combine(destination, "model.onnx")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DownloadValidateCommitAsync_SuccessReplacesExistingModelAfterValidation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "reazonspeech");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "working");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["model.onnx"] = [1, 2, 3]
            });
            var transaction = new ManagedModelFileTransaction(client);
            var validatedBeforeCommit = false;
            var validatedAfterCommit = false;

            await transaction.DownloadValidateCommitAsync(
                [new ManagedModelDownloadFile(new Uri("https://example.invalid/model.onnx"), "model.onnx", 3)],
                destination,
                temporaryRoot,
                staging =>
                {
                    validatedBeforeCommit = File.Exists(Path.Combine(staging, "model.onnx"))
                                            && File.Exists(Path.Combine(destination, "old.txt"));
                    return Task.CompletedTask;
                },
                validateCommittedAsync: committed =>
                {
                    validatedAfterCommit = File.Exists(Path.Combine(committed, "model.onnx"));
                    return Task.CompletedTask;
                });

            Assert.Multiple(() =>
            {
                Assert.That(validatedBeforeCommit, Is.True);
                Assert.That(validatedAfterCommit, Is.True);
                Assert.That(File.Exists(Path.Combine(destination, "old.txt")), Is.False);
                Assert.That(File.ReadAllBytes(Path.Combine(destination, "model.onnx")), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DownloadValidateCommitAsync_CancelRequestedDuringValidationDoesNotCommit()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "silero");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "working");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["silero_vad.onnx"] = [4, 5, 6]
            });
            var transaction = new ManagedModelFileTransaction(client);
            using var cancellation = new CancellationTokenSource();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await transaction.DownloadValidateCommitAsync(
                    [new ManagedModelDownloadFile(new Uri("https://example.invalid/silero_vad.onnx"), "silero_vad.onnx", 3)],
                    destination,
                    temporaryRoot,
                    _ =>
                    {
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    },
                    cancellationToken: cancellation.Token));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(destination, "old.txt")), Is.EqualTo("working"));
                Assert.That(File.Exists(Path.Combine(destination, "silero_vad.onnx")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DownloadValidateCommitAsync_WhenContentLengthsAvailable_ReportsOverallProgress()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "reazonspeech");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["encoder.onnx"] = [1, 2, 3],
                ["tokens.txt"] = [4, 5]
            });
            var transaction = new ManagedModelFileTransaction(client);
            var reports = new List<ManagedModelTransactionProgress>();
            var progress = new Progress<ManagedModelTransactionProgress>(reports.Add);

            await transaction.DownloadValidateCommitAsync(
                [
                    new ManagedModelDownloadFile(new Uri("https://example.invalid/encoder.onnx"), "encoder.onnx"),
                    new ManagedModelDownloadFile(new Uri("https://example.invalid/tokens.txt"), "tokens.txt")
                ],
                destination,
                temporaryRoot,
                _ => Task.CompletedTask,
                progress);

            // Progress<T>はSynchronizationContextがないテストではThreadPoolへdispatchするため、最終通知の到着だけ短く待つ。
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(reports.Any(x => x.TotalBytes == 5 && x.Percent is > 0 and <= 100), Is.True);
                Assert.That(reports.Any(x => x.CurrentFileName == "encoder.onnx"), Is.True);
                Assert.That(reports.Any(x => x.CurrentFileName == "tokens.txt"), Is.True);
                Assert.That(reports.Any(x => x.IsValidating), Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DownloadValidateCommitAsync_WhenAnyContentLengthUnavailable_ReportsIndeterminateProgress()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "silero");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(
                new Dictionary<string, byte[]>
                {
                    ["silero_vad.onnx"] = [1, 2, 3]
                },
                exposeHeadContentLength: false);
            var transaction = new ManagedModelFileTransaction(client);
            var reports = new List<ManagedModelTransactionProgress>();
            var progress = new Progress<ManagedModelTransactionProgress>(reports.Add);

            await transaction.DownloadValidateCommitAsync(
                [new ManagedModelDownloadFile(new Uri("https://example.invalid/silero_vad.onnx"), "silero_vad.onnx")],
                destination,
                temporaryRoot,
                _ => Task.CompletedTask,
                progress);

            await Task.Delay(50);
            Assert.That(reports.Any(x => x.TotalBytes is null && x.Percent is null), Is.True);
            Assert.That(reports.Any(x => x.IsValidating), Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteAtomically_RemovesOfficialPathOnlyAfterRename()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var destination = Path.Combine(root, "models", "silero-vad");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "silero_vad.onnx"), "model");
            var temporaryRoot = Path.Combine(root, ".model-ops");
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var transaction = new ManagedModelFileTransaction(client);

            transaction.DeleteAtomically(destination, temporaryRoot);

            Assert.That(Directory.Exists(destination), Is.False);
            Assert.That(Directory.Exists(temporaryRoot)
                        && Directory.EnumerateDirectories(temporaryRoot).Any(), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CleanupOwnedTemporaryDirectories_DeletesOnlyVoxArchiveGuidNamedDirectories()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var temporaryRoot = Path.Combine(root, ".model-ops");
            Directory.CreateDirectory(temporaryRoot);
            var download = Path.Combine(temporaryRoot, $"download-{Guid.NewGuid():N}");
            var backup = Path.Combine(temporaryRoot, $"backup-{Guid.NewGuid():N}");
            var deletion = Path.Combine(temporaryRoot, $"delete-{Guid.NewGuid():N}");
            var unrelated = Path.Combine(temporaryRoot, "keep-me");
            var spoofed = Path.Combine(temporaryRoot, "download-not-a-guid");
            foreach (var path in new[] { download, backup, deletion, unrelated, spoofed }) Directory.CreateDirectory(path);
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var transaction = new ManagedModelFileTransaction(client);

            transaction.CleanupOwnedTemporaryDirectories(temporaryRoot);

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(download), Is.False);
                Assert.That(Directory.Exists(backup), Is.False);
                Assert.That(Directory.Exists(deletion), Is.False);
                Assert.That(Directory.Exists(unrelated), Is.True);
                Assert.That(Directory.Exists(spoofed), Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HttpClient CreateClient(
        IReadOnlyDictionary<string, byte[]> responses,
        bool exposeHeadContentLength = true)
        => new(new StubHttpMessageHandler(request =>
        {
            var fileName = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (!responses.TryGetValue(fileName, out var content))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Head && !exposeHeadContentLength)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new UnknownLengthContent()
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }));

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
