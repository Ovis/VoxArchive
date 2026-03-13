using VoxArchive.Domain;

namespace VoxArchive.Application;

public sealed class InvalidRecordingStateTransitionException : InvalidOperationException
{
    public InvalidRecordingStateTransitionException(RecordingState current, RecordingState next)
        : base($"Invalid recording state transition: {current} -> {next}.")
    {
        CurrentState = current;
        NextState = next;
    }

    public RecordingState CurrentState { get; }
    public RecordingState NextState { get; }
}
