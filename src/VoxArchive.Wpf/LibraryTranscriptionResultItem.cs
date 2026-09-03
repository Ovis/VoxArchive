namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリ上で選択可能な文字起こし結果を表す
/// </summary>
public sealed class LibraryTranscriptionResultItem(TranscriptionResultMetadata metadata)
{
    /// <summary>正本JSONのパスを取得する</summary>
    public string DocumentPath => metadata.DocumentPath;

    /// <summary>エンジンの安定IDを取得する</summary>
    public string EngineId => metadata.EngineId;

    /// <summary>モデルの安定IDを取得する</summary>
    public string ModelId => metadata.ModelId;

    /// <summary>結果の作成日時を取得する</summary>
    public DateTimeOffset CreatedAt => metadata.CreatedAt;

    /// <summary>旧形式JSONから発見された結果かどうかを取得する</summary>
    public bool IsLegacy => metadata.IsLegacy;

    /// <summary>結果セレクタで使用する表示名を取得する</summary>
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
