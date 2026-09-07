namespace VoxArchive.Domain;

/// <summary>
/// 録音・再生・文字起こしに関する永続設定を保持する
/// </summary>
public sealed record RecordingOptions
{
    private TranscriptionSettings _transcription = new();

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
    public int ChannelAlignmentMilliseconds { get; init; }
    public string SpeakerDeviceId { get; init; } = string.Empty;
    public string MicDeviceId { get; init; } = string.Empty;
    public OutputCaptureMode OutputCaptureMode { get; init; } = OutputCaptureMode.SpeakerLoopback;
    public int? TargetProcessId { get; init; }
    public string StartStopHotkey { get; init; } = "Ctrl+F12";
    public double DefaultSpeakerPlaybackGainDb { get; init; }
    public double DefaultMicPlaybackGainDb { get; init; }
    public bool RecordingMetricsLogEnabled { get; init; }
    public bool SuppressCloseToTrayNotice { get; init; }
    public string FfmpegExecutablePath { get; init; } = string.Empty;

    /// <summary>
    /// 文字起こしの共通設定とEngine別設定を取得する
    /// </summary>
    /// <remarks>
    /// 旧settings.jsonのフラットな文字起こし項目はInfrastructureのmigration層で変換する。
    /// Domainへ旧accessorを残すとEngine固有型が再流入するため、現在形式ではこのプロパティだけを正本とする。
    /// </remarks>
    public TranscriptionSettings Transcription
    {
        get => _transcription;
        init => _transcription = value ?? new TranscriptionSettings();
    }
}
