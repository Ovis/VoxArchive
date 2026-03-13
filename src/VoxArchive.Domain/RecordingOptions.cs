namespace VoxArchive.Domain;

public sealed record RecordingOptions
{
    public string OutputDirectory { get; init; } = string.Empty;
    public int SampleRate { get; init; } = 48_000;
    public int BitDepth { get; init; } = 16;
    public int ChannelCount { get; init; } = 2;
    public int FrameMilliseconds { get; init; } = 10;
    public int TargetBufferMilliseconds { get; init; } = 200;
    public double MaxCorrectionPpm { get; init; } = 300;
    public double Kp { get; init; } = 2e-8;
    public double Ki { get; init; } = 1e-12;
    public int FlacCompressionLevel { get; init; } = 8;
    public string SpeakerDeviceId { get; init; } = string.Empty;
    public string MicDeviceId { get; init; } = string.Empty;
    public OutputCaptureMode OutputCaptureMode { get; init; } = OutputCaptureMode.SpeakerLoopback;
    public int? TargetProcessId { get; init; }
}
