namespace VoxArchive.Domain;

/// <summary>
/// 文字起こし全体の既定値とEngine別設定を保持する
/// </summary>
/// <remarks>
/// Engineを追加しても録音設定直下へEngine固有項目が増殖しないよう、
/// 共通設定とEngine固有設定をここで明示的に分離する。
/// </remarks>
public sealed record TranscriptionSettings
{
    /// <summary>文字起こし機能を有効にするかどうか</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>録音完了後に自動文字起こしを行うかどうか</summary>
    public bool AutoAfterRecord { get; init; }

    /// <summary>新規文字起こしで既定として利用するEngineの安定ID</summary>
    public string DefaultEngine { get; init; } = TranscriptionEngineId.Whisper.Value;

    /// <summary>Whisper固有の既定設定</summary>
    public WhisperTranscriptionSettings Whisper { get; init; } = new();

    /// <summary>ReazonSpeech固有の既定設定</summary>
    /// <remarks>
    /// Engine実装より先に設定の保存先を確定させることで、後続PRでRecordingOptionsの形を再変更せずに済む。
    /// </remarks>
    public ReazonSpeechTranscriptionSettings ReazonSpeech { get; init; } = new();

    /// <summary>認識結果から自動生成する派生出力形式</summary>
    public TranscriptionOutputFormats OutputFormats { get; init; } = TranscriptionOutputFormats.Txt;

    /// <summary>自動文字起こしの優先度</summary>
    public TranscriptionPriority AutoPriority { get; init; } = TranscriptionPriority.Low;

    /// <summary>手動文字起こしの優先度</summary>
    public TranscriptionPriority ManualPriority { get; init; } = TranscriptionPriority.Normal;

    /// <summary>文字起こし開始・完了の通知を表示するかどうか</summary>
    public bool ToastNotificationEnabled { get; init; } = true;

    /// <summary>文字起こし診断ログを有効にするかどうか</summary>
    public bool DiagnosticsLogEnabled { get; init; }
}

/// <summary>
/// Whisper固有の文字起こし既定値を保持する
/// </summary>
public sealed record WhisperTranscriptionSettings
{
    /// <summary>利用するWhisperモデル</summary>
    public TranscriptionModel Model { get; init; } = TranscriptionModel.Small;

    /// <summary>Whisper runtimeの要求モード</summary>
    public TranscriptionExecutionMode ExecutionMode { get; init; } = TranscriptionExecutionMode.Auto;

    /// <summary>
    /// 共通UIからWhisperへ渡す希望言語を保持する。空文字は言語を指定せずWhisperの自動判定へ委ねる
    /// </summary>
    /// <remarks>
    /// ReazonSpeechのような言語固定モデルはこの値を使用しない。現行の永続化構造では既存のWhisper設定領域を
    /// バッキングストアとして利用するが、ジョブではEngine非依存のTranscriptionLanguageとしてスナップショットする。
    /// </remarks>
    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// ReazonSpeech固有の文字起こし既定値を保持する
/// </summary>
/// <remarks>
/// 現段階ではEngine自体をまだ実装しないため、利用者が選択する論理モデルIDだけを保持する。
/// </remarks>
public sealed record ReazonSpeechTranscriptionSettings
{
    /// <summary>利用するReazonSpeech論理モデルID</summary>
    public string Model { get; init; } = "ja";
}
