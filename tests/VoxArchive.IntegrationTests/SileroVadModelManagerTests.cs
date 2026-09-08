using System.Net;
using VoxArchive.Transcription;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Silero VADモデルmanagerの状態cache、取得、削除、文字起こしとの排他を確認する
/// </summary>
public sealed class SileroVadModelManagerTests
{
    [Test]
    public void GetState_WhenModelDoesNotExist_ReturnsMissingWithoutValidation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
            var validationCount = 0;
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var manager = CreateManager(client, modelPath, _ => validationCount++);

            var state = manager.GetState();

            Assert.Multiple(() =>
            {
                Assert.That(state, Is.EqualTo(SileroVadModelState.Missing));
                Assert.That(validationCount, Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetState_WhenLoadSucceeds_CachesAvailableForSession()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root);
            var validationCount = 0;
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var manager = CreateManager(client, modelPath, _ => validationCount++);

            var first = manager.GetState();
            var second = manager.GetState();

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(SileroVadModelState.Available));
                Assert.That(second, Is.EqualTo(SileroVadModelState.Available));
                Assert.That(validationCount, Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetState_WhenLoadFails_CachesUnavailableForSession()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root);
            var validationCount = 0;
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var manager = CreateManager(client, modelPath, _ =>
            {
                validationCount++;
                throw new InvalidDataException("load failed");
            });

            var first = manager.GetState();
            var second = manager.GetState();

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(SileroVadModelState.Unavailable));
                Assert.That(second, Is.EqualTo(SileroVadModelState.Unavailable));
                Assert.That(validationCount, Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Recheck_ForcesNativeLoadAndRefreshesCachedState()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root);
            var shouldFail = true;
            var validationCount = 0;
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var manager = CreateManager(client, modelPath, _ =>
            {
                validationCount++;
                if (shouldFail) throw new InvalidDataException("load failed");
            });

            Assert.That(manager.GetState(), Is.EqualTo(SileroVadModelState.Unavailable));
            shouldFail = false;

            var rechecked = manager.Recheck();
            var cached = manager.GetState();

            Assert.Multiple(() =>
            {
                Assert.That(rechecked, Is.EqualTo(SileroVadModelState.Available));
                Assert.That(cached, Is.EqualTo(SileroVadModelState.Available));
                Assert.That(validationCount, Is.EqualTo(2));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Recheck_WhileTranscriptionReservationExists_IsRejected()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root);
            var usageTracker = new TranscriptionModelUsageTracker();
            using var reservation = usageTracker.Acquire(new(
                new("test-engine"),
                new("model-a")));
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var transaction = new ManagedModelFileTransaction(client);
            var manager = new SileroVadModelManager(transaction, usageTracker, modelPath, _ => { });

            Assert.That(
                () => manager.Recheck(),
                Throws.TypeOf<InvalidOperationException>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task InstallAsync_ValidationFailurePreservesExistingModel()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root, [9, 9, 9]);
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["silero_vad.onnx"] = [1, 2, 3, 4]
            });
            var manager = CreateManager(client, modelPath, path =>
            {
                // stagingだけ失敗させ、既存official modelがcommit前まで維持されることを確認する。
                if (!string.Equals(path, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("staging load failed");
                }
            });

            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await manager.InstallAsync(force: true));

            Assert.That(File.ReadAllBytes(modelPath), Is.EqualTo(new byte[] { 9, 9, 9 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task InstallAsync_SuccessValidatesStagingAndOfficialThenCachesAvailable()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
            using var client = CreateClient(new Dictionary<string, byte[]>
            {
                ["silero_vad.onnx"] = [1, 2, 3, 4]
            });
            var validatedPaths = new List<string>();
            var manager = CreateManager(client, modelPath, path =>
            {
                Assert.That(File.Exists(path), Is.True);
                validatedPaths.Add(path);
            });

            await manager.InstallAsync(force: false);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(modelPath), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(validatedPaths, Has.Count.EqualTo(2));
                Assert.That(validatedPaths[0], Is.Not.EqualTo(modelPath));
                Assert.That(validatedPaths[1], Is.EqualTo(modelPath));
                Assert.That(manager.GetState(), Is.EqualTo(SileroVadModelState.Available));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Delete_RemovesModelDirectoryAndInvalidatesState()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = CreateModelFile(root);
            var modelDirectory = Path.GetDirectoryName(modelPath)!;
            File.WriteAllText(Path.Combine(modelDirectory, "sidecar.txt"), "managed-state");
            using var client = CreateClient(new Dictionary<string, byte[]>());
            var manager = CreateManager(client, modelPath, _ => { });
            Assert.That(manager.GetState(), Is.EqualTo(SileroVadModelState.Available));

            manager.Delete();

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(modelDirectory), Is.False);
                Assert.That(manager.GetState(), Is.EqualTo(SileroVadModelState.Missing));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task InstallAsync_WhileDownloadIsActive_BlocksAdmissionStyleReservation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
            var usageTracker = new TranscriptionModelUsageTracker();
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new BlockingHttpMessageHandler(requestStarted, allowResponse));
            var manager = new SileroVadModelManager(
                new ManagedModelFileTransaction(client),
                usageTracker,
                modelPath,
                _ => { });

            var install = manager.InstallAsync(force: true);
            await requestStarted.Task;

            Assert.That(
                () => usageTracker.Acquire(new(new("test-engine"), new("model-a"))),
                Throws.TypeOf<InvalidOperationException>());

            allowResponse.SetResult();
            await install;

            using var reservation = usageTracker.Acquire(new(new("test-engine"), new("model-a")));
            Assert.That(reservation, Is.Not.Null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SileroVadModelManager CreateManager(
        HttpClient client,
        string modelPath,
        Action<string> validator)
        => new(
            new ManagedModelFileTransaction(client),
            new TranscriptionModelUsageTracker(),
            modelPath,
            validator);

    private static string CreateModelFile(string root, byte[]? content = null)
    {
        var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, content ?? [1, 2, 3]);
        return modelPath;
    }

    private static HttpClient CreateClient(IReadOnlyDictionary<string, byte[]> responses)
        => new(new StubHttpMessageHandler(request =>
        {
            var fileName = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return responses.TryGetValue(fileName, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
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

    private sealed class BlockingHttpMessageHandler(
        TaskCompletionSource requestStarted,
        TaskCompletionSource allowResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestStarted.TrySetResult();
            await allowResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
        }
    }
}
