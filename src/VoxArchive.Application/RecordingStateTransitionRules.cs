using VoxArchive.Domain;

namespace VoxArchive.Application;

public static class RecordingStateTransitionRules
{
    private static readonly IReadOnlyDictionary<RecordingState, HashSet<RecordingState>> AllowedTransitions =
        new Dictionary<RecordingState, HashSet<RecordingState>>
        {
            [RecordingState.Stopped] = [RecordingState.Starting, RecordingState.Error],
            [RecordingState.Starting] = [RecordingState.Recording, RecordingState.Error],
            [RecordingState.Recording] = [RecordingState.Pausing, RecordingState.Stopping, RecordingState.Error],
            [RecordingState.Pausing] = [RecordingState.Paused, RecordingState.Error],
            [RecordingState.Paused] = [RecordingState.Recording, RecordingState.Stopping, RecordingState.Error],
            [RecordingState.Stopping] = [RecordingState.Stopped, RecordingState.Error],
            [RecordingState.Error] = [RecordingState.Starting, RecordingState.Stopped]
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
