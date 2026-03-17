using VoxArchive.Domain;

namespace VoxArchive.Application;

public sealed class InvalidRecordingStateTransitionException(RecordingState current, RecordingState next)
    : InvalidOperationException($"Invalid recording state transition: {current} -> {next}.")
{
    public RecordingState CurrentState { get; } = current;
    public RecordingState NextState { get; } = next;
}
