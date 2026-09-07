using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリ上で選択可能な文字起こし結果を表す
/// </summary>
public sealed class LibraryTranscriptionResultItem(TranscriptionResultInfo result)
{
    /// <summary>正本JSONのパスを取得する</summary>
    public string DocumentPath => result.DocumentPath;

    /// <summary>エンジンの安定IDを取得する</summary>
    public string EngineId => result.EngineId;

    /// <summary>モデルの安定IDを取得する</summary>
    public string ModelId => result.ModelId ?? string.Empty;

    /// <summary>結果の作成日時を取得する</summary>
    public DateTimeOffset CreatedAt => result.CreatedAt;

    /// <summary>結果セレクタで使用する表示名を取得する</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ModelId)
        ? FormatEngineName(EngineId)
        : $"{FormatEngineName(EngineId)} / {FormatModelName(ModelId)}";

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
