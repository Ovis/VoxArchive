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

    /// <summary>Silero VADへ適用する共通発話検出設定</summary>
    public SileroVadSettings SileroVad { get; init; } = new();

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

/// <summary>
/// Silero VADのVoxArchive標準プロファイルとユーザー編集値を保持する
/// </summary>
/// <remarks>
/// モデルの配置状態や実行中の一時状態は設定ではないため保持しない。
/// 実行時にはAdmissionでopaque snapshotへ変換し、Queue投入後の設定変更からジョブを隔離する。
/// </remarks>
public sealed record SileroVadSettings
{
    /// <summary>発話と判定する確率閾値</summary>
    public double Threshold { get; init; } = 0.50d;

    /// <summary>発話として採用する最小継続時間（ms）</summary>
    public int MinimumSpeechDurationMilliseconds { get; init; } = 100;

    /// <summary>発話を分離する最小無音継続時間（ms）</summary>
    public int MinimumSilenceDurationMilliseconds { get; init; } = 500;

    /// <summary>検出した発話の前方へ原音から追加する余白（ms）</summary>
    public int PrePaddingMilliseconds { get; init; } = 300;

    /// <summary>検出した発話の後方へ原音から追加する余白（ms）</summary>
    public int PostPaddingMilliseconds { get; init; } = 200;
}
