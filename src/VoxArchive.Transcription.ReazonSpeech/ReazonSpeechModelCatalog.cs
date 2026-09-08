using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech k2-v2モデルの固定配布定義を提供する
/// </summary>
public static class ReazonSpeechModelCatalog
{
    private const string RepositoryBaseUrl = "https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve";
    internal const string Revision = "291488c8151be24d7da4bf7af26e533fad96e407";

    /// <summary>利用者向け日本語モデルID</summary>
    public static TranscriptionModelId JapaneseModelId { get; } = new("ja");

    /// <summary>FP32物理package ID</summary>
    public static TranscriptionModelId JapaneseFp32PackageId { get; } = new("ja-fp32");

    /// <summary>INT8物理package ID</summary>
    public static TranscriptionModelId JapaneseInt8PackageId { get; } = new("ja-int8");

    /// <summary>encoder/joiner INT8 + decoder FP32物理package ID</summary>
    public static TranscriptionModelId JapaneseInt8Fp32PackageId { get; } = new("ja-int8-fp32");

    /// <summary>実行時に利用可能なprecision別物理package定義</summary>
    internal static IReadOnlyList<ReazonSpeechManagedModelPackage> Packages { get; } =
    [
        CreatePackage(
            JapaneseFp32PackageId,
            ReazonSpeechPrecision.Fp32,
            "encoder-epoch-99-avg-1.onnx",
            "decoder-epoch-99-avg-1.onnx",
            "joiner-epoch-99-avg-1.onnx"),
        CreatePackage(
            JapaneseInt8PackageId,
            ReazonSpeechPrecision.Int8,
            "encoder-epoch-99-avg-1.int8.onnx",
            "decoder-epoch-99-avg-1.int8.onnx",
            "joiner-epoch-99-avg-1.int8.onnx"),
        CreatePackage(
            JapaneseInt8Fp32PackageId,
            ReazonSpeechPrecision.Int8Fp32,
            "encoder-epoch-99-avg-1.int8.onnx",
            "decoder-epoch-99-avg-1.onnx",
            "joiner-epoch-99-avg-1.int8.onnx")
    ];

    /// <summary>指定precisionに対応する物理package IDを返す</summary>
    public static TranscriptionModelId GetPackageId(ReazonSpeechPrecision precision)
        => precision switch
        {
            ReazonSpeechPrecision.Fp32 => JapaneseFp32PackageId,
            ReazonSpeechPrecision.Int8 => JapaneseInt8PackageId,
            ReazonSpeechPrecision.Int8Fp32 => JapaneseInt8Fp32PackageId,
            _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, "未対応のReazonSpeech precisionです。")
        };

    private static ReazonSpeechManagedModelPackage CreatePackage(
        TranscriptionModelId packageId,
        ReazonSpeechPrecision precision,
        string encoder,
        string decoder,
        string joiner)
        => new(
            packageId,
            precision,
            [CreateFile(encoder), CreateFile(decoder), CreateFile(joiner), CreateFile("tokens.txt")]);

    private static ManagedModelDownloadFile CreateFile(string fileName)
        => new(new Uri($"{RepositoryBaseUrl}/{Revision}/{fileName}?download=true"), fileName);
}

/// <summary>
/// ReazonSpeechで1回のRecognizer初期化に必要な物理ファイル集合を保持する
/// </summary>
internal sealed record ReazonSpeechManagedModelPackage(
    TranscriptionModelId PackageId,
    ReazonSpeechPrecision Precision,
    IReadOnlyList<ManagedModelDownloadFile> Files);
