using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper Engineが1回の認識で参照するimmutableな設定snapshotを保持する
/// </summary>
public sealed record WhisperEngineOptions : ITranscriptionEngineOptions
{
    /// <summary>利用する論理モデルID</summary>
    public TranscriptionModelId ModelId { get; init; } = new("small");

    /// <summary>Job Admissionで解決したモデルファイルの絶対パス</summary>
    public string? ModelPath { get; init; }

    /// <summary>Whisper runtimeへ要求する実行方式</summary>
    public WhisperExecutionMode ExecutionMode { get; init; } = WhisperExecutionMode.Auto;

    /// <summary>Whisperへ渡す言語。空文字は自動判定を意味する</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>native診断ログを取得するかどうか</summary>
    public bool DiagnosticsEnabled { get; init; }
}

/// <summary>
/// Whisperへ要求するbackend選択方針を定義する
/// </summary>
public enum WhisperExecutionMode
{
    Auto = 0,
    Cpu = 1,
    Cuda = 2,
    Vulkan = 3,
}

/// <summary>
/// Whisper Engineが公開する安定識別子を保持する
/// </summary>
public static class WhisperEngineIdentity
{
    /// <summary>Whisper Engineの安定ID</summary>
    public static TranscriptionEngineId EngineId { get; } = new("whisper");
}
