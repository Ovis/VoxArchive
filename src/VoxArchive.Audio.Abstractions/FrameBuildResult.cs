namespace VoxArchive.Audio.Abstractions;

public sealed record FrameBuildResult(
    ReadOnlyMemory<byte> InterleavedPcm16,
    int SpeakerSamplesRead,
    int MicSamplesConsumed,
    int MicSamplesRequested,
    int UnderflowSamples,
    int OverflowSamples,
    double AppliedPpm,
    double SpeakerLevel,
    double MicLevel);
