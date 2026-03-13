using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

public interface IRecordingTelemetrySink
{
    void OnStateChanged(RecordingState state);
    void OnStatistics(RecordingStatistics statistics);
    void OnError(string message);
}
