using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech optionsと複数ファイルmodel packageの関係をEngine project内で解決する
/// </summary>
public sealed class ReazonSpeechModelRequirementResolver : ITranscriptionModelRequirementResolver
{
    /// <inheritdoc />
    public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options)
        => GetOptions(options).ModelId;

    /// <inheritdoc />
    public ITranscriptionEngineOptions SelectModel(
        ITranscriptionEngineOptions options,
        TranscriptionModelId modelId)
        => GetOptions(options) with
        {
            ModelId = modelId,
            EncoderPath = null,
            DecoderPath = null,
            JoinerPath = null,
            TokensPath = null
        };

    /// <inheritdoc />
    public ITranscriptionEngineOptions BindInstallation(
        ITranscriptionEngineOptions options,
        TranscriptionModelInstallation installation)
    {
        var reazon = GetOptions(options);
        if (installation.EngineId != ReazonSpeechEngineIdentity.EngineId || installation.ModelId != reazon.ModelId)
        {
            throw new InvalidOperationException("ReazonSpeech optionsとモデル配置の識別子が一致しません。");
        }

        return reazon with
        {
            EncoderPath = Find(installation.Files, "encoder-", ".onnx"),
            DecoderPath = Find(installation.Files, "decoder-", ".onnx"),
            JoinerPath = Find(installation.Files, "joiner-", ".onnx"),
            TokensPath = installation.Files.SingleOrDefault(x =>
                string.Equals(Path.GetFileName(x), "tokens.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("ReazonSpeech tokens.txtを解決できません。")
        };
    }

    private static string Find(IReadOnlyList<string> files, string prefix, string suffix)
        => files.SingleOrDefault(path =>
               Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidDataException($"ReazonSpeechモデルファイルを解決できません: {prefix}*{suffix}");

    private static ReazonSpeechEngineOptions GetOptions(ITranscriptionEngineOptions options)
        => options as ReazonSpeechEngineOptions
           ?? throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));
}
