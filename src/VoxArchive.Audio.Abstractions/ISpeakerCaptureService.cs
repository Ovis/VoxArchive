using VoxArchive.Domain;

namespace VoxArchive.Audio.Abstractions;

public interface ISpeakerCaptureService
{
    event EventHandler<CaptureChunk>? ChunkCaptured;
    Task StartAsync(string speakerDeviceId, int sampleRate, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
