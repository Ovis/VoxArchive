namespace VoxArchive.Domain;

/// <summary>
/// 文字起こし全体の共通設定とEngine別settings blobを保持する
/// </summary>
public sealed record TranscriptionSettings
{
    /// <summary>文字起こし機能を有効にするかどうか</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>録音完了後に自動文字起こしを行うかどうか</summary>
    public bool AutoAfterRecord { get; init; }

    /// <summary>新規文字起こしで既定利用するEngineの安定ID</summary>
    public string DefaultEngine { get; init; } = "whisper";

    /// <summary>
    /// 利用者が文字起こしについて希望する言語。Engineへ直接渡すparameterではない
    /// </summary>
    public string PreferredLanguage { get; init; } = string.Empty;

    /// <summary>
    /// Engine IDをkeyとするopaque settingsを保持する
    /// </summary>
    /// <remarks>
    /// DomainはEngine固有のmodelやexecution modeを解釈しない。
    /// Engine固有settingsの検証・serializeは各Engineのsettings providerが担当する。
    /// </remarks>
    public IReadOnlyDictionary<string, TranscriptionEngineSettings> Engines { get; init; }
        = new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase);

    /// <summary>canonical JSONから自動生成する派生出力形式</summary>
    public TranscriptionOutputFormats OutputFormats { get; init; } = TranscriptionOutputFormats.Txt;

    /// <summary>自動文字起こしの優先度</summary>
    public TranscriptionPriority AutoPriority { get; init; } = TranscriptionPriority.Low;

    /// <summary>手動文字起こしの優先度</summary>
    public TranscriptionPriority ManualPriority { get; init; } = TranscriptionPriority.Normal;

    /// <summary>文字起こし開始・完了通知を表示するかどうか</summary>
    public bool ToastNotificationEnabled { get; init; } = true;

    /// <summary>文字起こし診断ログを有効にするかどうか</summary>
    public bool DiagnosticsLogEnabled { get; init; }
}
