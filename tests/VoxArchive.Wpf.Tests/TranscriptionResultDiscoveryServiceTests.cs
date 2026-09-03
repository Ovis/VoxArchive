using System.Text.Json;
using VoxArchive.Wpf;
using Xunit;

namespace VoxArchive.Wpf.Tests;

/// <summary>
/// canonical/legacy文字起こし結果の発見規則を検証する
/// </summary>
public sealed class TranscriptionResultDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsCanonicalAndLegacyResultsNewestFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"voxarchive-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "recording.flac");
            await File.WriteAllBytesAsync(audioPath, []);
            var legacyPath = Path.Combine(directory, "recording-small.json");
            await File.WriteAllTextAsync(legacyPath, "{\"segments\":[]}");
            File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddMinutes(-10));

            var canonicalPath = Path.Combine(directory, "recording-large-v3.json");
            await File.WriteAllTextAsync(canonicalPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                source = new { fileName = "recording.flac" },
                transcription = new { engine = "whisper", model = "large-v3" },
                runtime = new { requested = "auto", actual = (string?)null },
                createdAt = DateTimeOffset.UtcNow,
                segments = Array.Empty<object>()
            }));

            var service = new TranscriptionResultDiscoveryService();
            var results = await service.DiscoverAsync(audioPath);

            Assert.Equal(2, results.Count);
            Assert.Equal("large-v3", results[0].ModelId);
            Assert.False(results[0].IsLegacy);
            Assert.Equal("small", results[1].ModelId);
            Assert.True(results[1].IsLegacy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresOtherRecordingsAndBrokenJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"voxarchive-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "recording.flac");
            await File.WriteAllBytesAsync(audioPath, []);
            await File.WriteAllTextAsync(Path.Combine(directory, "recording-small.json"), "not-json");
            await File.WriteAllTextAsync(Path.Combine(directory, "other-small.json"), "{\"segments\":[]}");

            var service = new TranscriptionResultDiscoveryService();
            var results = await service.DiscoverAsync(audioPath);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
