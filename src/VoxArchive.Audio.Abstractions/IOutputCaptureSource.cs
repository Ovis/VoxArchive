using VoxArchive.Domain;

namespace VoxArchive.Audio.Abstractions;

public interface IOutputCaptureSource
{
    OutputCaptureMode Mode { get; }
    event EventHandler<CaptureChunk>? ChunkCaptured;
    event EventHandler? SourceUnavailable;

    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
