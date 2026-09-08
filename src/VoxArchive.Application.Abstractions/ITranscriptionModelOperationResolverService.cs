namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFがEngine内部の物理package IDを知らずにモデル管理対象を解決するUse Caseを定義する
/// </summary>
public interface ITranscriptionModelOperationResolverService
{
    /// <summary>
    /// 論理モデルIDと現在の編集設定から、モデル管理APIへ渡す内部モデルIDを解決する
    /// </summary>
    string ResolveModelId(
        string engineId,
        string logicalModelId,
        IReadOnlyDictionary<string, string> advancedSettings);
}
