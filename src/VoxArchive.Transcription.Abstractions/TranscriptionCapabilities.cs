using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// Engine固有設定のdeserializeとvalidationを担当する
/// </summary>
public interface ITranscriptionEngineSettingsProvider
{
    /// <summary>
    /// 永続化されたJSONから実行用options snapshotを生成する
    /// </summary>
    ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion);

    /// <summary>
    /// settingsとして成立しているか検証する
    /// </summary>
    IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options);
}

/// <summary>
/// Engine固有モデルのcatalogと物理操作をCommonへ公開するoptional capability
/// </summary>
public interface ITranscriptionModelProvider
{
    /// <summary>
    /// Engine IDを取得する
    /// </summary>
    TranscriptionEngineId EngineId { get; }
}

/// <summary>
/// PreferredLanguageをEngine固有設定へ解決するoptional capability
/// </summary>
public interface ITranscriptionLanguageCapability
{
    /// <summary>
    /// 指定言語をEngineが扱えるか判定する
    /// </summary>
    bool Supports(string? preferredLanguage);
}

/// <summary>
/// Job Admission時の実行環境検証を担当するoptional capability
/// </summary>
public interface ITranscriptionEngineExecutionValidator
{
    /// <summary>
    /// 現在の環境で指定optionsを実行可能か検証する
    /// </summary>
    Task<IReadOnlyList<TranscriptionValidationError>> ValidateAsync(
        ITranscriptionEngineOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine固有診断情報を取得するoptional capability
/// </summary>
public interface ITranscriptionEngineDiagnostics
{
    /// <summary>
    /// UIへ投影可能なEngine非依存診断結果を取得する
    /// </summary>
    Task<IReadOnlyList<TranscriptionDiagnosticItem>> DiagnoseAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine registrationの構成要素をまとめる
/// </summary>
public sealed record TranscriptionEngineRegistration(
    ITranscriptionEngine Engine,
    ITranscriptionEngineSettingsProvider SettingsProvider,
    ITranscriptionModelProvider? ModelProvider = null,
    ITranscriptionEngineDiagnostics? Diagnostics = null,
    ITranscriptionLanguageCapability? LanguageCapability = null,
    ITranscriptionEngineExecutionValidator? ExecutionValidator = null);

/// <summary>
/// Engine非依存のvalidation errorを表す
/// </summary>
public sealed record TranscriptionValidationError(string Code, string Message);

/// <summary>
/// Engine非依存の診断項目を表す
/// </summary>
public sealed record TranscriptionDiagnosticItem(
    string Code,
    string Message,
    TranscriptionDiagnosticSeverity Severity);

/// <summary>
/// 診断項目の重大度を定義する
/// </summary>
public enum TranscriptionDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}
