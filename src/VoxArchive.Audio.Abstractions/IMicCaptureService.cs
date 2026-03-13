using VoxArchive.Domain;

namespace VoxArchive.Audio.Abstractions;

public interface IMicCaptureService
{
    event EventHandler<CaptureChunk>? ChunkCaptured;
    Task StartAsync(string micDeviceId, int sampleRate, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
