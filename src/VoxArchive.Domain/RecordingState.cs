namespace VoxArchive.Domain;

public enum RecordingState
{
    Stopped,
    Starting,
    Recording,
    Pausing,
    Paused,
    Stopping,
    Error
}
