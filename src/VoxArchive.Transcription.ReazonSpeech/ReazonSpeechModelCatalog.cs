using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech k2-v2モデルの固定配布定義を提供する
/// </summary>
public static class ReazonSpeechModelCatalog
{
    private const string RepositoryBaseUrl = "https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve";
    private const string Revision = "291488c8151be24d7da4bf7af26e533fad96e407";

    /// <summary>日本語モデルID</summary>
    public static TranscriptionModelId JapaneseModelId { get; } = new("ja");

    /// <summary>選択可能なReazonSpeechモデル定義</summary>
    public static IReadOnlyList<TranscriptionModelPackageDefinition> All { get; } = [CreateJapanese()];

    private static TranscriptionModelPackageDefinition CreateJapanese()
        => new(
            ReazonSpeechEngineIdentity.EngineId,
            JapaneseModelId,
            "日本語（k2-v2）",
            "k2-v2",
            Revision,
            "Apache-2.0",
            [
                CreateFile("encoder-epoch-99-avg-1.int8.onnx", 154_670_139, "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d"),
                CreateFile("decoder-epoch-99-avg-1.onnx", 11_767_836, "58b18211ae06265466bfa17172dab574df94f76c8bcb61a3640c28ba860e4124"),
                CreateFile("joiner-epoch-99-avg-1.int8.onnx", 2_696_970, "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de"),
                CreateFile("tokens.txt", 45_754, "2c3ac659818a48a0c04010e0593bbc4d7c8a24a054340b01131499c05fd52def")
            ]);

    private static TranscriptionModelFileDefinition CreateFile(string fileName, long size, string sha256)
        => new(new Uri($"{RepositoryBaseUrl}/{Revision}/{fileName}?download=true"), fileName, size, sha256);
}
