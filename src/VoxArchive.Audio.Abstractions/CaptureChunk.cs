namespace VoxArchive.Audio.Abstractions;

public readonly record struct CaptureChunk(
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    DateTimeOffset TimestampUtc);
