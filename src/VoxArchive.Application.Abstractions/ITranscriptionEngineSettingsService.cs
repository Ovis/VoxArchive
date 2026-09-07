using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFがEngine固有settings JSONを解釈せずに文字起こしEngine設定を編集するためのUse Caseを定義する
/// </summary>
public interface ITranscriptionEngineSettingsService
{
    /// <summary>opaque settingsをUI向けの共通設定値へ投影する</summary>
    TranscriptionEngineConfigurationInfo GetConfiguration(string engineId, TranscriptionEngineSettings settings);

    /// <summary>UIで選択されたモデル・実行方式をEngine capability経由でopaque settingsへ反映する</summary>
    TranscriptionEngineSettings UpdateConfiguration(
        string engineId,
        TranscriptionEngineSettings settings,
        string? modelId,
        string? executionModeId);
}

/// <summary>
/// Engine固有settingsをUIがJSONとして解釈せずに表示するための共通投影を表す
/// </summary>
public sealed record TranscriptionEngineConfigurationInfo(
    string? ModelId,
    string? ExecutionModeId,
    IReadOnlyList<TranscriptionExecutionModeInfo> ExecutionModes);

/// <summary>Engineが公開する実行方式の安定IDと表示名を表す</summary>
public sealed record TranscriptionExecutionModeInfo(string Id, string DisplayName);
