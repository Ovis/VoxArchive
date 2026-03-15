namespace VoxArchive.Wpf;

public sealed record TranscriptionJobResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string> GeneratedFiles,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);
