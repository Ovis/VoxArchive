namespace VoxArchive.Wpf;

public sealed record WhisperEnvironmentStatus(
    bool RuntimeAvailable,
    bool ModelInstalled,
    string RuntimeMessage,
    string ModelMessage,
    string DetailMessage);
