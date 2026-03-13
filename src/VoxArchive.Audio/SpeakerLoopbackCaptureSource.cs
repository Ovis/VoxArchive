using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Audio;

public sealed class SpeakerLoopbackCaptureSource : IOutputCaptureSource
{
    private readonly ISpeakerCaptureService _speakerCaptureService;

    public SpeakerLoopbackCaptureSource(ISpeakerCaptureService speakerCaptureService)
    {
        _speakerCaptureService = speakerCaptureService;
        _speakerCaptureService.ChunkCaptured += OnChunkCaptured;
    }

    public OutputCaptureMode Mode => OutputCaptureMode.SpeakerLoopback;

    public event EventHandler<CaptureChunk>? ChunkCaptured;
    public event EventHandler? SourceUnavailable;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        await _speakerCaptureService.StartAsync(options.SpeakerDeviceId, options.SampleRate, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _speakerCaptureService.StopAsync(cancellationToken);
    }

    public void NotifyUnavailable(string reason = "SpeakerSourceUnavailable")
    {
        _ = reason;
        SourceUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnChunkCaptured(object? sender, CaptureChunk chunk)
    {
        ChunkCaptured?.Invoke(this, chunk);
    }
}
