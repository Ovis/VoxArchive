using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

public interface IRecordingService
{
    RecordingState CurrentState { get; }
    bool IsSpeakerCaptureEnabled { get; }
    bool IsMicCaptureEnabled { get; }

    event EventHandler<RecordingState>? StateChanged;
    event EventHandler<RecordingStatistics>? StatisticsUpdated;
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<OutputSourceChangedEvent>? OutputSourceChanged;

    Task<string> StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    void SetSpeakerCaptureEnabled(bool enabled);
    void SetMicCaptureEnabled(bool enabled);
}
