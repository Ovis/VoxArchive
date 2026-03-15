using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed record TranscriptionJobRequest(
    string AudioFilePath,
    RecordingOptions Options,
    TranscriptionTrigger Trigger);
