using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// VoxArchiveが対応するWhisperモデルの固定配布定義を提供する
/// </summary>
/// <remarks>
/// モデル取得後の完全性検証と文字起こし開始前のサイズ検証を同じ定義から行えるよう、
/// 配布元revision・期待サイズ・SHA-256をアプリ側で固定する。
/// </remarks>
public static class WhisperModelCatalog
{
    private const string Revision = "c521a4b02f422512d734391fdf08bb08c0862f68";
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + Revision + "/";

    /// <summary>VoxArchiveが選択肢として公開するWhisperモデルを取得する</summary>
    public static IReadOnlyList<TranscriptionModelDefinition> All { get; } =
    [
        Create("tiny", "Tiny", "ggml-tiny.bin", 77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        Create("base", "Base", "ggml-base.bin", 147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        Create("small", "Small", "ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        Create("medium", "Medium", "ggml-medium.bin", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        Create("large-v3", "Large V3", "ggml-large-v3.bin", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2")
    ];

    private static TranscriptionModelDefinition Create(
        string modelId,
        string displayName,
        string fileName,
        long size,
        string sha256)
    {
        return new TranscriptionModelDefinition(
            TranscriptionEngineId.Whisper,
            new TranscriptionModelId(modelId),
            displayName,
            modelId,
            Revision,
            "MIT",
            [new TranscriptionModelFileDefinition(new Uri(BaseUrl + fileName + "?download=true"), fileName, size, sha256)]);
    }
}
