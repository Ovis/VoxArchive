using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFがEngine固有options型やsettings JSONを解釈せず、詳細設定を読み書きするUse Caseを定義する
/// </summary>
public interface ITranscriptionEngineAdvancedSettingsService
{
    /// <summary>保存済みEngine設定を安定ID/valueへ投影する</summary>
    IReadOnlyDictionary<string, string> GetValues(string engineId, TranscriptionEngineSettings settings);

    /// <summary>安定ID/valueを保存用Engine設定へ反映する</summary>
    TranscriptionEngineSettings UpdateValues(
        string engineId,
        TranscriptionEngineSettings settings,
        IReadOnlyDictionary<string, string> values);
}
