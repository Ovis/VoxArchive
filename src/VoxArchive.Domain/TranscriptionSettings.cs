using System.Text.Json.Serialization;

namespace VoxArchive.Domain;

/// <summary>
/// 文字起こし全体の共通設定とEngine別settings blobを保持する
/// </summary>
public sealed record TranscriptionSettings
{
    public bool Enabled { get; init; } = true;
    public bool AutoAfterRecord { get; init; }

    /// <summary>新規文字起こしで既定利用するEngineの安定ID</summary>
    public string DefaultEngine { get; init; } = "whisper";

    /// <summary>
    /// 利用者が文字起こしについて希望する言語。Engineへ直接渡すparameterではない
    /// </summary>
    public string PreferredLanguage { get; init; } = string.Empty;

    /// <summary>Engine IDをkeyとするopaque settings</summary>
    public IReadOnlyDictionary<string, TranscriptionEngineSettings> Engines { get; init; }
        = new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase);

    public TranscriptionOutputFormats OutputFormats { get; init; } = TranscriptionOutputFormats.Txt;
    public TranscriptionPriority AutoPriority { get; init; } = TranscriptionPriority.Low;
    public TranscriptionPriority ManualPriority { get; init; } = TranscriptionPriority.Normal;
    public bool ToastNotificationEnabled { get; init; } = true;
    public bool DiagnosticsLogEnabled { get; init; }

    // 以下はcall-site移行中だけのメモリ上互換アクセサーであり、settings.jsonには出力しない。
    // 永続化形式へWhisper固有型を再混入させないためJsonSettingsServiceがraw JSONから一度だけ構築する。
    [JsonIgnore]
    [Obsolete("Use Engines[\"whisper\"] and PreferredLanguage.")]
    public WhisperTranscriptionSettings Whisper { get; init; } = new();

    [JsonIgnore]
    [Obsolete("Use Engines[\"reazonspeech\"].")]
    public ReazonSpeechTranscriptionSettings ReazonSpeech { get; init; } = new();
}

/// <summary>旧call-site移行中だけ保持するWhisper設定DTO</summary>
[Obsolete("Legacy settings migration only.")]
public sealed record WhisperTranscriptionSettings
{
    public TranscriptionModel Model { get; init; } = TranscriptionModel.Small;
    public TranscriptionExecutionMode ExecutionMode { get; init; } = TranscriptionExecutionMode.Auto;
    public string Language { get; init; } = string.Empty;
}

/// <summary>旧call-site移行中だけ保持するReazonSpeech設定DTO</summary>
[Obsolete("Legacy settings migration only.")]
public sealed record ReazonSpeechTranscriptionSettings
{
    public string Model { get; init; } = "ja";
}
