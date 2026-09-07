using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech Engineが1回の認識で参照するimmutableな設定snapshotを保持する
/// </summary>
public sealed record ReazonSpeechEngineOptions : ITranscriptionEngineOptions
{
    /// <summary>利用する論理モデルID</summary>
    public TranscriptionModelId ModelId { get; init; } = new("ja");

    /// <summary>Job Admissionで解決したencoderモデルパス</summary>
    public string? EncoderPath { get; init; }

    /// <summary>Job Admissionで解決したdecoderモデルパス</summary>
    public string? DecoderPath { get; init; }

    /// <summary>Job Admissionで解決したjoinerモデルパス</summary>
    public string? JoinerPath { get; init; }

    /// <summary>Job Admissionで解決したtokensファイルパス</summary>
    public string? TokensPath { get; init; }
}

/// <summary>
/// ReazonSpeech Engineが公開する安定識別子を保持する
/// </summary>
public static class ReazonSpeechEngineIdentity
{
    /// <summary>ReazonSpeech Engineの安定ID</summary>
    public static TranscriptionEngineId EngineId { get; } = new("reazonspeech");
}
