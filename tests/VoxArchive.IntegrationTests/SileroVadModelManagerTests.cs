using System.Net;
using VoxArchive.Transcription;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Silero VADモデルmanagerがnative load結果をsession cacheし、明示再確認で更新できることを確認する
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
            using var client = CreateClient([]);
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
            using var client = CreateClient([]);
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
            using var client = CreateClient([]);
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
            using var client = CreateClient([]);
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
            using var client = CreateClient([]);
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

    private static SileroVadModelManager CreateManager(
        HttpClient client,
        string modelPath,
        Action<string> validator)
        => new(
            new ManagedModelFileTransaction(client),
            new TranscriptionModelUsageTracker(),
            modelPath,
            validator);

    private static string CreateModelFile(string root)
    {
        var modelPath = Path.Combine(root, "silero-vad", "silero_vad.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, [1, 2, 3]);
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
}
