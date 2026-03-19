using VoxArchive.Domain;

namespace VoxArchive.Application;

public sealed class RecordingStateMachine(RecordingState initialState = RecordingState.Stopped)
{
    private readonly Lock _gate = new();
    private RecordingState _state = initialState;

    public RecordingState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool TryTransition(RecordingState nextState, out RecordingState previousState)
    {
        lock (_gate)
        {
            previousState = _state;
            if (!RecordingStateTransitionRules.CanTransition(_state, nextState))
            {
                return false;
            }

            _state = nextState;
            return true;
        }
    }

    public RecordingState Transition(RecordingState nextState)
    {
        lock (_gate)
        {
            var previous = _state;
            RecordingStateTransitionRules.EnsureCanTransition(previous, nextState);
            _state = nextState;
            return previous;
        }
    }
}
