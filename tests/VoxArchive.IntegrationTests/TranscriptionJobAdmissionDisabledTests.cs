using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 文字起こし無効時のAdmission境界を確認する
/// </summary>
public sealed class TranscriptionJobAdmissionDisabledTests
{
    [Test]
    public async Task AdmitAsync_TranscriptionDisabled_IsRejectedBeforeEngineResolution()
    {
        var registry = new TranscriptionEngineRegistry([]);
        var usageTracker = new TranscriptionModelUsageTracker();
        var service = new TranscriptionJobAdmissionService(
            registry,
            new TranscriptionModelManager(registry, usageTracker),
            usageTracker);
        var options = new RecordingOptions
        {
            Transcription = new TranscriptionSettings
            {
                Enabled = false,
                DefaultEngine = "engine-that-does-not-exist"
            }
        };

        var result = await service.AdmitAsync(
            "recording.flac",
            options,
            TranscriptionTrigger.Manual);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Job, Is.Null);
            Assert.That(result.MissingModel, Is.Null);
            Assert.That(result.Message, Does.Contain("文字起こし機能が無効"));
            Assert.That(result.Message, Does.Not.Contain("Engine"));
        });
    }
}
