namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 利用者向け論理モデルと編集中設定から、モデル管理操作で使用する物理モデルIDを解決するoptional capability
/// </summary>
/// <remarks>
/// ReazonSpeechのようにprecisionによって物理packageが変わるEngineで、内部package IDをPresentationへ漏らさないための境界である。
/// Admission時の実行モデル解決とは別に、まだ保存されていないUI編集値をモデル取得・再確認へ反映する用途で使用する。
/// </remarks>
public interface ITranscriptionModelOperationCapability
{
    /// <summary>このcapabilityが属するEngine IDを取得する</summary>
    TranscriptionEngineId EngineId { get; }

    /// <summary>
    /// 論理モデルIDとadvanced settingsからモデル管理操作用の物理モデルIDを解決する
    /// </summary>
    TranscriptionModelId ResolveModel(
        TranscriptionModelId logicalModelId,
        IReadOnlyDictionary<string, string> advancedSettings);
}
