namespace VoxArchive.Domain;

public sealed record RecordingOptions
{
    public string OutputDirectory { get; init; } = string.Empty;
    public int SampleRate { get; init; } = 48_000;
    public int BitDepth { get; init; } = 16;
    public int ChannelCount { get; init; } = 2;
    public int FrameMilliseconds { get; init; } = 10;
    public int TargetBufferMilliseconds { get; init; } = 80;
    public double MaxCorrectionPpm { get; init; } = 300;
    public double Kp { get; init; } = 2e-8;
    public double Ki { get; init; } = 1e-12;
    public int FlacCompressionLevel { get; init; } = 8;
    public int ChannelAlignmentMilliseconds { get; init; } = 0;
    public string SpeakerDeviceId { get; init; } = string.Empty;
    public string MicDeviceId { get; init; } = string.Empty;
    public OutputCaptureMode OutputCaptureMode { get; init; } = OutputCaptureMode.SpeakerLoopback;
    public int? TargetProcessId { get; init; }
    public string StartStopHotkey { get; init; } = "Ctrl+F12";
    public double DefaultSpeakerPlaybackGainDb { get; init; } = 0d;
    public double DefaultMicPlaybackGainDb { get; init; } = 0d;
    public bool RecordingMetricsLogEnabled { get; init; } = false;
    public bool TranscriptionEnabled { get; init; } = true;
    public bool AutoTranscriptionAfterRecord { get; init; } = false;
    public TranscriptionExecutionMode TranscriptionExecutionMode { get; init; } = TranscriptionExecutionMode.Auto;
    public TranscriptionModel TranscriptionModel { get; init; } = TranscriptionModel.Small;
    public string TranscriptionLanguage { get; init; } = "ja";
    public TranscriptionOutputFormats TranscriptionOutputFormats { get; init; } = TranscriptionOutputFormats.Txt;
    public TranscriptionPriority AutoTranscriptionPriority { get; init; } = TranscriptionPriority.Low;
    public TranscriptionPriority ManualTranscriptionPriority { get; init; } = TranscriptionPriority.Normal;
    public bool TranscriptionToastNotificationEnabled { get; init; } = true;
    public bool SuppressCloseToTrayNotice { get; init; } = false;
}
