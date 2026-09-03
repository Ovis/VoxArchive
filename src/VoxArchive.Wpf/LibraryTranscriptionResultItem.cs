namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリ上で選択可能な文字起こし結果を表す
/// </summary>
public sealed class LibraryTranscriptionResultItem(TranscriptionResultMetadata metadata)
{
    public string DocumentPath => metadata.DocumentPath;
    public string EngineId => metadata.EngineId;
    public string ModelId => metadata.ModelId;
    public DateTimeOffset CreatedAt => metadata.CreatedAt;
    public bool IsLegacy => metadata.IsLegacy;
    public string DisplayName => $"{FormatEngineName(EngineId)} / {FormatModelName(ModelId)}";

    private static string FormatEngineName(string engineId) => engineId switch
    {
        "whisper" => "Whisper",
        "reazonspeech" => "ReazonSpeech",
        _ => engineId
    };

    private static string FormatModelName(string modelId) => modelId switch
    {
        "ja" => "日本語",
        "ja-en" => "日本語・英語",
        _ => modelId
    };
}
