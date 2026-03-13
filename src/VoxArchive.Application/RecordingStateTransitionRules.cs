using VoxArchive.Domain;

namespace VoxArchive.Application;

public static class RecordingStateTransitionRules
{
    private static readonly IReadOnlyDictionary<RecordingState, HashSet<RecordingState>> AllowedTransitions =
        new Dictionary<RecordingState, HashSet<RecordingState>>
        {
            [RecordingState.Stopped] = new() { RecordingState.Starting, RecordingState.Error },
            [RecordingState.Starting] = new() { RecordingState.Recording, RecordingState.Error },
            [RecordingState.Recording] = new() { RecordingState.Pausing, RecordingState.Stopping, RecordingState.Error },
            [RecordingState.Pausing] = new() { RecordingState.Paused, RecordingState.Error },
            [RecordingState.Paused] = new() { RecordingState.Recording, RecordingState.Stopping, RecordingState.Error },
            [RecordingState.Stopping] = new() { RecordingState.Stopped, RecordingState.Error },
            [RecordingState.Error] = new() { RecordingState.Starting, RecordingState.Stopped }
        };

    public static bool CanTransition(RecordingState current, RecordingState next)
    {
        if (current == next)
        {
            return false;
        }

        return AllowedTransitions.TryGetValue(current, out var nextStates) && nextStates.Contains(next);
    }

    public static void EnsureCanTransition(RecordingState current, RecordingState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidRecordingStateTransitionException(current, next);
        }
    }
}
