using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// canonical transcription JSONとして永続化するEngine非依存ドキュメントを表す
/// </summary>
public sealed record TranscriptionDocument
{
    /// <summary>
    /// Application versionとは独立したcanonical schema version
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>canonical schema version</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>元録音ファイル名</summary>
    public required string SourceFileName { get; init; }

    /// <summary>認識に使用したEngine ID</summary>
    public required string EngineId { get; init; }

    /// <summary>モデルを利用するEngineの場合のModel ID</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// 再文字起こしで当時の要求条件を復元するためのEngine非依存snapshot
    /// </summary>
    /// <remarks>
    /// Engine固有settingsはopaque JSONとして保持し、Commonは内容を解釈しない。
    /// PreferredLanguageはEngine parameterではなく利用者の共通intentなのでsettings blobとは分離して保存する。
    /// </remarks>
    public TranscriptionExecutionSnapshot? ExecutionSnapshot { get; init; }

    /// <summary>ドキュメントを確定した時刻</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Engineが返したopaque metadata。Commonは内容を解釈せず、そのまま正本へ保存する
    /// </summary>
    public IReadOnlyDictionary<string, object?>? EngineMetadata { get; init; }

    /// <summary>認識segment一覧</summary>
    public IReadOnlyList<TranscriptionDocumentSegment> Segments { get; init; } = [];
}

/// <summary>
/// 再文字起こし時に当時の要求条件を再構築するためのopaque snapshotを表す
/// </summary>
/// <param name="EngineSettingsSchemaVersion">Engine settingsのschema version</param>
/// <param name="EngineSettings">Engine固有設定。Commonでは内容を解釈しない</param>
/// <param name="PreferredLanguage">Admission時に利用者が指定していた共通言語intent</param>
public sealed record TranscriptionExecutionSnapshot(
    int EngineSettingsSchemaVersion,
    JsonElement EngineSettings,
    string PreferredLanguage);

/// <summary>
/// canonical documentへ保存する1つの認識segmentを表す
/// </summary>
public sealed record TranscriptionDocumentSegment(
    double Start,
    double End,
    string Text,
    string? Speaker);

/// <summary>
/// Common artifact生成でサポートする派生形式を定義する
/// </summary>
[Flags]
public enum TranscriptionArtifactFormats
{
    None = 0,
    Txt = 1 << 0,
    Srt = 1 << 1,
    Vtt = 1 << 2,
}

/// <summary>
/// 1 Jobのartifact生成条件を保持する
/// </summary>
public sealed record TranscriptionArtifactOptions(
    TranscriptionModelId? ModelId,
    TranscriptionArtifactFormats Formats,
    string? FileNameSuffix = null,
    TranscriptionExecutionSnapshot? ExecutionSnapshot = null);
