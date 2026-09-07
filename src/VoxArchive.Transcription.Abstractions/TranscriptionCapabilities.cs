using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// Engine固有設定とtyped execution optionsの相互変換・validationを担当する
/// </summary>
public interface ITranscriptionEngineSettingsProvider
{
    /// <summary>永続化されたJSONから実行用options snapshotを生成する</summary>
    ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion);

    /// <summary>typed optionsを現行schemaの永続化JSONへ変換する</summary>
    JsonElement Serialize(ITranscriptionEngineOptions options);

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
/// typed Engine optionsから実行時に必要な物理モデルを解決し、モデル選択と物理配置をoptionsへ反映するoptional capability
/// </summary>
/// <remarks>
/// Common/ApplicationがWhisperやReazonSpeechのoptions型へdowncastしないための境界である。
/// managed modelを必要としないEngineはこのcapabilityを登録しない。
/// ReazonSpeechのように1つの利用者向け論理モデルがprecisionごとに異なる物理packageを要求する場合、
/// ResolveRequiredModelは実行時package IDを返し、利用者向け選択値はITranscriptionModelSelectionCapabilityで分離する。
/// </remarks>
public interface ITranscriptionModelRequirementResolver
{
    /// <summary>指定optionsが実行時に必要とする物理モデルpackage IDを取得する</summary>
    TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options);

    /// <summary>保存済み結果などで指定された利用者向け論理モデルIDをoptions snapshotへ反映した新しいoptionsを返す</summary>
    ITranscriptionEngineOptions SelectModel(
        ITranscriptionEngineOptions options,
        TranscriptionModelId modelId);

    /// <summary>取得済みモデルの物理配置をoptions snapshotへ反映した新しいoptionsを返す</summary>
    ITranscriptionEngineOptions BindInstallation(
        ITranscriptionEngineOptions options,
        TranscriptionModelInstallation installation);
}

/// <summary>
/// 利用者が設定画面で選択する論理モデルIDを実行時package IDから分離して公開するoptional capability
/// </summary>
/// <remarks>
/// 通常のEngineではModelRequirementResolverのIDと同一なので未登録でよい。
/// precision等によって物理packageが切り替わるEngineだけが実装し、UIへ内部package IDを露出させない。
/// </remarks>
public interface ITranscriptionModelSelectionCapability
{
    /// <summary>指定optionsで利用者が選択している論理モデルIDを取得する</summary>
    TranscriptionModelId GetSelectedModel(ITranscriptionEngineOptions options);
}

/// <summary>
/// Engineが利用者へ選択可能な実行方式を公開するoptional capability
/// </summary>
/// <remarks>
/// UIがWhisper等のtyped optionsやsettings JSONを直接解釈しないための境界である。
/// 実行方式を選択できないEngineはこのcapabilityを登録しない。
/// </remarks>
public interface ITranscriptionExecutionModeCapability
{
    /// <summary>UIで選択可能な実行方式を返す</summary>
    IReadOnlyList<TranscriptionExecutionModeDescriptor> GetAvailableModes();

    /// <summary>指定optionsで要求されている実行方式の安定IDを返す</summary>
    string GetSelectedMode(ITranscriptionEngineOptions options);

    /// <summary>指定実行方式をoptions snapshotへ反映した新しいoptionsを返す</summary>
    ITranscriptionEngineOptions SelectMode(ITranscriptionEngineOptions options, string modeId);
}

/// <summary>UIへ公開する実行方式の安定IDと表示名を表す</summary>
public sealed record TranscriptionExecutionModeDescriptor(string Id, string DisplayName);

/// <summary>PreferredLanguageの対応可否とEngine固有optionsへの解決を行うoptional capability</summary>
public interface ITranscriptionLanguageCapability
{
    bool Supports(string? preferredLanguage);
    ITranscriptionEngineOptions Resolve(ITranscriptionEngineOptions options, string? preferredLanguage);
}

/// <summary>Job Admission時の実行環境検証を担当するoptional capability</summary>
public interface ITranscriptionEngineExecutionValidator
{
    Task<IReadOnlyList<TranscriptionValidationError>> ValidateAsync(ITranscriptionEngineOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Engine固有診断情報を取得するoptional capability</summary>
public interface ITranscriptionEngineDiagnostics
{
    Task<IReadOnlyList<TranscriptionDiagnosticItem>> DiagnoseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Engineが従来から利用しているartifact file name suffixをCommonへ提供するoptional capability
/// </summary>
/// <remarks>
/// CommonがEngine名やモデル命名規則を知るとEngine追加のたびに共通層の変更が必要になるため、
/// 既存UXとの互換性が必要な命名規則はEngine側で決定する。
/// </remarks>
public interface ITranscriptionArtifactNamingCapability
{
    /// <summary>拡張子を含まないartifact suffixを返す</summary>
    string BuildFileNameSuffix(TranscriptionModelId? modelId);
}

/// <summary>Engine registrationの構成要素をまとめる</summary>
public sealed record TranscriptionEngineRegistration(
    ITranscriptionEngine Engine,
    ITranscriptionEngineSettingsProvider SettingsProvider,
    ITranscriptionModelProvider? ModelProvider = null,
    ITranscriptionModelRequirementResolver? ModelRequirementResolver = null,
    ITranscriptionEngineDiagnostics? Diagnostics = null,
    ITranscriptionLanguageCapability? LanguageCapability = null,
    ITranscriptionEngineExecutionValidator? ExecutionValidator = null,
    ITranscriptionArtifactNamingCapability? ArtifactNamingCapability = null,
    ITranscriptionExecutionModeCapability? ExecutionModeCapability = null,
    ITranscriptionModelSelectionCapability? ModelSelectionCapability = null);

public sealed record TranscriptionValidationError(string Code, string Message);
public sealed record TranscriptionDiagnosticItem(string Code, string Message, TranscriptionDiagnosticSeverity Severity);
public enum TranscriptionDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}