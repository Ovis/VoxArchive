using System.Text.Json.Serialization;

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
    public TranscriptionSettings Transcription
    {
        get => _transcription;
        init => _transcription = value ?? new TranscriptionSettings();
    }

    // 以下のプロパティは既存コードとの段階的な互換用アクセサーであり、settings.jsonには出力しない。
    // 永続化形式の旧フィールドはJsonSettingsServiceが読み込み時にTranscriptionへ移行する。
    [JsonIgnore]
    public bool TranscriptionDiagnosticsLogEnabled
    {
        get => Transcription.DiagnosticsLogEnabled;
        init => _transcription = _transcription with { DiagnosticsLogEnabled = value };
    }

    [JsonIgnore]
    public bool TranscriptionEnabled
    {
        get => Transcription.Enabled;
        init => _transcription = _transcription with { Enabled = value };
    }

    [JsonIgnore]
    public bool AutoTranscriptionAfterRecord
    {
        get => Transcription.AutoAfterRecord;
        init => _transcription = _transcription with { AutoAfterRecord = value };
    }

    [JsonIgnore]
    public TranscriptionExecutionMode TranscriptionExecutionMode
    {
        get => Transcription.Whisper.ExecutionMode;
        init => _transcription = _transcription with { Whisper = _transcription.Whisper with { ExecutionMode = value } };
    }

    [JsonIgnore]
    public TranscriptionModel TranscriptionModel
    {
        get => Transcription.Whisper.Model;
        init => _transcription = _transcription with { Whisper = _transcription.Whisper with { Model = value } };
    }

    [JsonIgnore]
    public string TranscriptionLanguage
    {
        get => Transcription.Whisper.Language;
        init => _transcription = _transcription with { Whisper = _transcription.Whisper with { Language = value } };
    }

    [JsonIgnore]
    public TranscriptionOutputFormats TranscriptionOutputFormats
    {
        get => Transcription.OutputFormats;
        init => _transcription = _transcription with { OutputFormats = value };
    }

    [JsonIgnore]
    public TranscriptionPriority AutoTranscriptionPriority
    {
        get => Transcription.AutoPriority;
        init => _transcription = _transcription with { AutoPriority = value };
    }

    [JsonIgnore]
    public TranscriptionPriority ManualTranscriptionPriority
    {
        get => Transcription.ManualPriority;
        init => _transcription = _transcription with { ManualPriority = value };
    }

    [JsonIgnore]
    public bool TranscriptionToastNotificationEnabled
    {
        get => Transcription.ToastNotificationEnabled;
        init => _transcription = _transcription with { ToastNotificationEnabled = value };
    }
}
