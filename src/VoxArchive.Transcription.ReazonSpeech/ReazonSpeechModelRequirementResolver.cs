using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech optionsとprecision別物理model packageの関係をEngine project内で解決する
/// </summary>
public sealed class ReazonSpeechModelRequirementResolver : ITranscriptionModelRequirementResolver
{
    private const string EncoderFp32 = "encoder-epoch-99-avg-1.onnx";
    private const string EncoderInt8 = "encoder-epoch-99-avg-1.int8.onnx";
    private const string DecoderFp32 = "decoder-epoch-99-avg-1.onnx";
    private const string DecoderInt8 = "decoder-epoch-99-avg-1.int8.onnx";
    private const string JoinerFp32 = "joiner-epoch-99-avg-1.onnx";
    private const string JoinerInt8 = "joiner-epoch-99-avg-1.int8.onnx";
    private const string Tokens = "tokens.txt";

    /// <inheritdoc />
    public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options)
        => ReazonSpeechModelCatalog.GetPackageId(GetOptions(options).Precision);

    /// <inheritdoc />
    public ITranscriptionEngineOptions SelectModel(
        ITranscriptionEngineOptions options,
        TranscriptionModelId modelId)
    {
        if (modelId != ReazonSpeechModelCatalog.JapaneseModelId)
        {
            throw new NotSupportedException($"未対応のReazonSpeech論理モデルです: {modelId}");
        }

        return GetOptions(options) with
        {
            ModelId = modelId,
            EncoderPath = null,
            DecoderPath = null,
            JoinerPath = null,
            TokensPath = null
        };
    }

    /// <inheritdoc />
    public ITranscriptionEngineOptions BindInstallation(
        ITranscriptionEngineOptions options,
        TranscriptionModelInstallation installation)
    {
        var reazon = GetOptions(options);
        var expectedPackageId = ReazonSpeechModelCatalog.GetPackageId(reazon.Precision);
        if (installation.EngineId != ReazonSpeechEngineIdentity.EngineId || installation.ModelId != expectedPackageId)
        {
            throw new InvalidOperationException(
                $"ReazonSpeech optionsとモデル配置の識別子が一致しません。期待={expectedPackageId}, 実際={installation.ModelId}");
        }

        var requiredFiles = GetRequiredFileNames(reazon.Precision);
        return reazon with
        {
            EncoderPath = FindExact(installation.Files, requiredFiles.Encoder),
            DecoderPath = FindExact(installation.Files, requiredFiles.Decoder),
            JoinerPath = FindExact(installation.Files, requiredFiles.Joiner),
            TokensPath = FindExact(installation.Files, Tokens)
        };
    }

    /// <summary>
    /// 指定precisionでsherpa-onnxへ渡すモデルファイル名を返す
    /// </summary>
    internal static ReazonSpeechRequiredModelFiles GetRequiredFileNames(ReazonSpeechPrecision precision)
        => precision switch
        {
            ReazonSpeechPrecision.Fp32 => new(EncoderFp32, DecoderFp32, JoinerFp32),
            ReazonSpeechPrecision.Int8 => new(EncoderInt8, DecoderInt8, JoinerInt8),
            ReazonSpeechPrecision.Int8Fp32 => new(EncoderInt8, DecoderFp32, JoinerInt8),
            _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, "未対応のReazonSpeech precisionです。")
        };

    private static string FindExact(IReadOnlyList<string> files, string fileName)
        => files.SingleOrDefault(path =>
               string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidDataException($"ReazonSpeechモデルファイルを解決できません: {fileName}");

    private static ReazonSpeechEngineOptions GetOptions(ITranscriptionEngineOptions options)
        => options as ReazonSpeechEngineOptions
           ?? throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));
}

/// <summary>
/// 1つのReazonSpeech precisionで必要なONNXファイル名を保持する
/// </summary>
internal sealed record ReazonSpeechRequiredModelFiles(
    string Encoder,
    string Decoder,
    string Joiner);
