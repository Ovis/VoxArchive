using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech Engineが1回の認識で参照するimmutableな設定snapshotを保持する
/// </summary>
public sealed record ReazonSpeechEngineOptions : ITranscriptionEngineOptions
{
    /// <summary>利用する論理モデルID</summary>
    public TranscriptionModelId ModelId { get; init; } = new("ja");

    /// <summary>利用するモデル精度構成</summary>
    public ReazonSpeechPrecision Precision { get; init; } = ReazonSpeechPrecision.Int8Fp32;

    /// <summary>利用するdecoding method</summary>
    public ReazonSpeechDecodingMethod DecodingMethod { get; init; } = ReazonSpeechDecodingMethod.GreedySearch;

    /// <summary>modified beam searchで探索する最大active path数</summary>
    public int MaxActivePaths { get; init; } = 4;

    /// <summary>sherpa-onnxがCPU推論に使用するthread数</summary>
    public int CpuThreads { get; init; } = Math.Min(4, Environment.ProcessorCount);

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
/// ReazonSpeech K2モデルの精度構成を表す
/// </summary>
public enum ReazonSpeechPrecision
{
    /// <summary>encoder/decoder/joinerをFP32で使用する</summary>
    Fp32 = 0,

    /// <summary>encoder/decoder/joinerをINT8で使用する</summary>
    Int8 = 1,

    /// <summary>encoder/joinerをINT8、decoderをFP32で使用する</summary>
    Int8Fp32 = 2
}

/// <summary>
/// ReazonSpeech K2で利用可能なdecoding methodを表す
/// </summary>
public enum ReazonSpeechDecodingMethod
{
    /// <summary>公式ReazonSpeechと同じgreedy searchを使用する</summary>
    GreedySearch = 0,

    /// <summary>modified beam searchを使用する</summary>
    ModifiedBeamSearch = 1
}

/// <summary>
/// ReazonSpeech Engineが公開する安定識別子を保持する
/// </summary>
public static class ReazonSpeechEngineIdentity
{
    /// <summary>ReazonSpeech Engineの安定ID</summary>
    public static TranscriptionEngineId EngineId { get; } = new("reazonspeech");
}
