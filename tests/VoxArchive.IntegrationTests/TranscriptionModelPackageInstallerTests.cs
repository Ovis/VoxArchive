using System.Net;
using System.Security.Cryptography;
using TextEncoding = System.Text.Encoding;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class TranscriptionModelPackageInstallerTests
{
    [Test]
    public async Task InstallAsync_VerifiesAllFilesAndPublishesCompleteDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["https://example.invalid/encoder.onnx"] = TextEncoding.UTF8.GetBytes("encoder"),
                ["https://example.invalid/tokens.txt"] = TextEncoding.UTF8.GetBytes("tokens")
            };
            using var httpClient = new HttpClient(new DictionaryHandler(files));
            var installer = new TranscriptionModelPackageInstaller(httpClient);
            var definition = CreateDefinition(files);

            var installedDirectory = await installer.InstallAsync(definition, root);

            Assert.Multiple(() =>
            {
                Assert.That(installedDirectory, Is.EqualTo(Path.Combine(root, "reazonspeech", "ja")));
                Assert.That(installer.IsInstalled(definition, installedDirectory), Is.True);
                Assert.That(File.Exists(Path.Combine(installedDirectory, "encoder.onnx")), Is.True);
                Assert.That(File.Exists(Path.Combine(installedDirectory, "tokens.txt")), Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void InstallAsync_HashMismatchDoesNotPublishPartialModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["https://example.invalid/encoder.onnx"] = TextEncoding.UTF8.GetBytes("corrupted")
            };
            using var httpClient = new HttpClient(new DictionaryHandler(files));
            var installer = new TranscriptionModelPackageInstaller(httpClient);
            var definition = new TranscriptionModelDefinition(
                TranscriptionEngineId.ReazonSpeech,
                new TranscriptionModelId("ja"),
                "日本語",
                "k2-v2",
                "test-revision",
                "Apache-2.0",
                [new TranscriptionModelFileDefinition(new Uri("https://example.invalid/encoder.onnx"), "encoder.onnx", files.Values.Single().Length, new string('0', 64))]);

            Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(definition, root));
            Assert.That(Directory.Exists(Path.Combine(root, "reazonspeech", "ja")), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranscriptionModelDefinition CreateDefinition(IReadOnlyDictionary<string, byte[]> files)
        => new(
            TranscriptionEngineId.ReazonSpeech,
            new TranscriptionModelId("ja"),
            "日本語",
            "k2-v2",
            "test-revision",
            "Apache-2.0",
            files.Select(pair => new TranscriptionModelFileDefinition(
                new Uri(pair.Key),
                Path.GetFileName(new Uri(pair.Key).AbsolutePath),
                pair.Value.Length,
                Convert.ToHexString(SHA256.HashData(pair.Value)))).ToArray());

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DictionaryHandler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null || !responses.TryGetValue(request.RequestUri.AbsoluteUri, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
