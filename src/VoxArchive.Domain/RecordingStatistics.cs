namespace VoxArchive.Domain;

public sealed record RecordingStatistics
{
    public TimeSpan ElapsedTime { get; init; }
    public long EstimatedFileSizeBytes { get; init; }
    public double SpeakerLevel { get; init; }
    public double MicLevel { get; init; }
    public double SpeakerBufferMilliseconds { get; init; }
    public double MicBufferMilliseconds { get; init; }
    public double DriftCorrectionPpm { get; init; }
    public long UnderflowCount { get; init; }
    public long OverflowCount { get; init; }
    public string? OutputFilePath { get; init; }
}
