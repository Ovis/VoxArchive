namespace VoxArchive.Domain;

public sealed record ProcessInfo(
    int ProcessId,
    string ApplicationName,
    string ExecutableName,
    string? WindowTitle);
