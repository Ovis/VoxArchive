using VoxArchive.Domain;

namespace VoxArchive.Audio.Abstractions;

public interface IOutputCaptureFailoverCoordinator
{
    event EventHandler<OutputSourceChangedEvent>? SourceChanged;

    Task<OutputCaptureMode> ResolveStartupModeAsync(RecordingOptions options, CancellationToken cancellationToken = default);
    Task HandleProcessCaptureUnavailableAsync(CancellationToken cancellationToken = default);
}
