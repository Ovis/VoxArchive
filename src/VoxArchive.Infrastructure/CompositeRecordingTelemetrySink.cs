using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class CompositeRecordingTelemetrySink : IRecordingTelemetrySink
{
    private readonly IReadOnlyList<IRecordingTelemetrySink> _sinks;

    public CompositeRecordingTelemetrySink(params IRecordingTelemetrySink[] sinks)
    {
        _sinks = sinks;
    }

    public void OnStateChanged(RecordingState state)
    {
        foreach (var sink in _sinks)
        {
            sink.OnStateChanged(state);
        }
    }

    public void OnStatistics(RecordingStatistics statistics)
    {
        foreach (var sink in _sinks)
        {
            sink.OnStatistics(statistics);
        }
    }

    public void OnError(string message)
    {
        foreach (var sink in _sinks)
        {
            sink.OnError(message);
        }
    }
}
