using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしEngineごとのモデル配置・取得処理を共通の境界として公開する
/// </summary>
/// <remarks>
/// Whisperは単一ファイル、ReazonSpeechは複数ファイルで構成されるため、呼び出し側が物理構成を仮定しないようにする。
/// モデル固有のダウンロード元や配置規則は各Providerへ閉じ込める。
/// </remarks>
public interface ITranscriptionModelProvider
{
    /// <summary>このProviderが管理するEngineの安定IDを取得する</summary>
    TranscriptionEngineId EngineId { get; }

    /// <summary>指定した論理モデルが利用可能な状態で配置されているか確認する</summary>
    bool IsInstalled(TranscriptionModelId modelId);

    /// <summary>
    /// 指定した論理モデルを利用可能にし、配置されたモデルファイル群を返す
    /// </summary>
    Task<TranscriptionModelInstallation> InstallAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default);

    /// <summary>指定した論理モデルを削除する</summary>
    Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 配置済みモデルの物理ファイル群を取得する
    /// </summary>
    /// <exception cref="InvalidOperationException">モデルが完全な状態で配置されていない場合</exception>
    TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId);
}

/// <summary>
/// 1つの論理モデルとして利用する物理ファイル群を表す
/// </summary>
/// <param name="EngineId">モデルを提供するEngineの安定ID</param>
/// <param name="ModelId">利用者が選択する論理モデルの安定ID</param>
/// <param name="Files">モデルを構成する物理ファイルの絶対パス</param>
public sealed record TranscriptionModelInstallation(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    IReadOnlyList<string> Files)
{
    /// <summary>
    /// 単一ファイルモデルで利用する先頭ファイルを取得する
    /// </summary>
    public string PrimaryFile => Files.Count > 0
        ? Files[0]
        : throw new InvalidOperationException("モデルを構成するファイルがありません。");
}
