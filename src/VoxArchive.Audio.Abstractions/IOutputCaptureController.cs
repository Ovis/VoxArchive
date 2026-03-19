using VoxArchive.Domain;

namespace VoxArchive.Audio.Abstractions;

public interface IOutputCaptureController
{
    OutputCaptureMode CurrentMode { get; }
    event EventHandler<CaptureChunk>? ChunkCaptured;
    event EventHandler<OutputSourceChangedEvent>? SourceChanged;

    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);
    Task SwitchToSpeakerLoopbackAsync(string reason, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
