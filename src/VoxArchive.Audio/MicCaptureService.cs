using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class MicCaptureService : IMicCaptureService
{
    private volatile bool _running;

    public event EventHandler<CaptureChunk>? ChunkCaptured;

    public Task StartAsync(string micDeviceId, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(micDeviceId))
        {
            throw new ArgumentException("micDeviceId is required.", nameof(micDeviceId));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public void PublishTestChunk(ReadOnlyMemory<float> samples, int sampleRate)
    {
        if (!_running)
        {
            return;
        }

        ChunkCaptured?.Invoke(this, new CaptureChunk(samples, sampleRate, DateTimeOffset.UtcNow));
    }
}
