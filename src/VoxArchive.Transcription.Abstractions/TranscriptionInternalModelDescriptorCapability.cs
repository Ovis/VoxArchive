namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 利用者向けcatalogへ公開しない物理モデルpackageのdescriptorをCommonへ提供するoptional capability
/// </summary>
/// <remarks>
/// ReazonSpeechのように、UIでは論理モデルだけを見せつつ実行時・モデル管理時にはprecision別の物理packageを扱うEngine向けである。
/// 通常のEngineは実装せず、CommonはGetAvailableModelsの公開catalogをそのまま利用する。
/// </remarks>
public interface ITranscriptionInternalModelDescriptorCapability
{
    /// <summary>
    /// 指定された内部モデルIDのdescriptorを解決する
    /// </summary>
    /// <param name="modelId">実行時またはモデル管理時に使用する物理モデルID</param>
    /// <returns>解決できた場合はdescriptor、未対応の場合はnull</returns>
    TranscriptionModelDescriptor? ResolveInternalDescriptor(TranscriptionModelId modelId);
}
