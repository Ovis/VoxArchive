using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// Engine固有設定のdeserializeとvalidationを担当する
/// </summary>
public interface ITranscriptionEngineSettingsProvider
{
    /// <summary>永続化されたJSONから実行用options snapshotを生成する</summary>
    ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion);

    /// <summary>settingsとして成立しているか検証する</summary>
    IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options);
}

/// <summary>
/// Engine固有モデルのcatalogと物理操作をCommonへ公開するoptional capability
/// </summary>
public interface ITranscriptionModelProvider
{
    TranscriptionEngineId EngineId { get; }
    IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels();
    bool IsReady(TranscriptionModelId modelId);
    TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level);
    Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default);
    TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId);
}

/// <summary>
/// typed Engine optionsから必要モデルを解決し、取得済み物理配置を実行optionsへ束縛するoptional capability
/// </summary>
/// <remarks>
/// Common/ApplicationがWhisperやReazonSpeechのoptions型へdowncastしないための境界である。
/// managed modelを必要としないEngineはこのcapabilityを登録しない。
/// </remarks>
public interface ITranscriptionModelRequirementResolver
{
    /// <summary>指定optionsが必要とする論理モデルIDを取得する</summary>
    TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options);

    /// <summary>取得済みモデルの物理配置をoptions snapshotへ反映した新しいoptionsを返す</summary>
    ITranscriptionEngineOptions BindInstallation(
        ITranscriptionEngineOptions options,
        TranscriptionModelInstallation installation);
}

/// <summary>
/// PreferredLanguageの対応可否とEngine固有optionsへの解決を行うoptional capability
/// </summary>
public interface ITranscriptionLanguageCapability
{
    /// <summary>指定言語をEngineが扱えるか判定する</summary>
    bool Supports(string? preferredLanguage);

    /// <summary>
    /// PreferredLanguageをEngine固有の実行optionsへ反映する
    /// </summary>
    ITranscriptionEngineOptions Resolve(
        ITranscriptionEngineOptions options,
        string? preferredLanguage);
}

/// <summary>
/// Job Admission時の実行環境検証を担当するoptional capability
/// </summary>
public interface ITranscriptionEngineExecutionValidator
{
    Task<IReadOnlyList<TranscriptionValidationError>> ValidateAsync(
        ITranscriptionEngineOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine固有診断情報を取得するoptional capability
/// </summary>
public interface ITranscriptionEngineDiagnostics
{
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
    ITranscriptionModelRequirementResolver? ModelRequirementResolver = null,
    ITranscriptionEngineDiagnostics? Diagnostics = null,
    ITranscriptionLanguageCapability? LanguageCapability = null,
    ITranscriptionEngineExecutionValidator? ExecutionValidator = null);

/// <summary>Engine非依存のvalidation errorを表す</summary>
public sealed record TranscriptionValidationError(string Code, string Message);

/// <summary>Engine非依存の診断項目を表す</summary>
public sealed record TranscriptionDiagnosticItem(
    string Code,
    string Message,
    TranscriptionDiagnosticSeverity Severity);

/// <summary>診断項目の重大度を定義する</summary>
public enum TranscriptionDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}
