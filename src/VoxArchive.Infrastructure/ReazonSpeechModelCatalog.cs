using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

/// <summary>
/// VoxArchiveが公式配布元から取得するReazonSpeechモデルの固定定義を提供する
/// </summary>
/// <remarks>
/// 配布元のmainブランチを追従すると同じ論理ModelIdでも実体が変化し得るため、
/// URL・revision・サイズ・SHA-256をすべて固定し、取得後の完全性検証に利用する。
/// </remarks>
public static class ReazonSpeechModelCatalog
{
    /// <summary>日本語ReazonSpeech k2-v2モデルの論理ID</summary>
    public static readonly TranscriptionModelId JapaneseModelId = new("ja");

    /// <summary>
    /// 日本語ReazonSpeech k2-v2モデルの固定定義を取得する
    /// </summary>
    public static TranscriptionModelDefinition Japanese { get; } = CreateJapaneseDefinition();

    /// <summary>
    /// VoxArchiveが現在サポートするReazonSpeechモデル定義を取得する
    /// </summary>
    public static IReadOnlyList<TranscriptionModelDefinition> All { get; } = [Japanese];

    private const string RepositoryBaseUrl = "https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve";
    private const string Revision = "291488c8151be24d7da4bf7af26e533fad96e407";

    private static TranscriptionModelDefinition CreateJapaneseDefinition()
    {
        // sherpa-onnxの利用例と同じ構成を基準にしつつ、CPU負荷と容量を抑えるため
        // encoder/joinerはINT8、精度への影響を避けたいdecoderはFP32を採用する。
        return new TranscriptionModelDefinition(
            TranscriptionEngineId.ReazonSpeech,
            JapaneseModelId,
            "日本語（k2-v2）",
            "k2-v2",
            Revision,
            "Apache-2.0",
            [
                CreateFile(
                    "encoder-epoch-99-avg-1.int8.onnx",
                    154_670_139,
                    "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d"),
                CreateFile(
                    "decoder-epoch-99-avg-1.onnx",
                    11_767_836,
                    "58b18211ae06265466bfa17172dab574df94f76c8bcb61a3640c28ba860e4124"),
                CreateFile(
                    "joiner-epoch-99-avg-1.int8.onnx",
                    2_696_970,
                    "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de"),
                CreateFile(
                    "tokens.txt",
                    45_754,
                    "2c3ac659818a48a0c04010e0593bbc4d7c8a24a054340b01131499c05fd52def")
            ]);
    }

    private static TranscriptionModelFileDefinition CreateFile(string fileName, long size, string sha256)
    {
        return new TranscriptionModelFileDefinition(
            new Uri($"{RepositoryBaseUrl}/{Revision}/{fileName}?download=true"),
            fileName,
            size,
            sha256);
    }
}
