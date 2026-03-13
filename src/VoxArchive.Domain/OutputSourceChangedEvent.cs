namespace VoxArchive.Domain;

public sealed record OutputSourceChangedEvent(
    OutputCaptureMode Previous,
    OutputCaptureMode Current,
    string Reason,
    DateTimeOffset OccurredAt);
