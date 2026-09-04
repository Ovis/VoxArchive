using VoxArchive.Domain;
using VoxArchive.Infrastructure;

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

    /// <summary>設定画面で選択可能なモデル一覧を取得する</summary>
    IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels();

    /// <summary>指定した論理モデルが文字起こし実行に必要なサイズ検証を満たしているか確認する</summary>
    bool IsInstalled(TranscriptionModelId modelId);

    /// <summary>
    /// 指定した検証レベルでモデル配置状態を確認する
    /// </summary>
    TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level);

    /// <summary>
    /// 指定した論理モデルを利用可能にし、配置されたモデルファイル群を返す
    /// </summary>
    Task<TranscriptionModelInstallation> InstallAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 進捗通知と再取得指定を伴ってモデルを配置する
    /// </summary>
    Task<TranscriptionModelInstallation> InstallManagedAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>指定した論理モデルを削除する</summary>
    Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 配置済みモデルの物理ファイル群を取得する
    /// </summary>
    /// <exception cref="InvalidOperationException">モデルが文字起こし実行可能な状態で配置されていない場合</exception>
    TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId);
}

/// <summary>
/// UIへ公開する論理モデルの安定IDと表示名を表す
/// </summary>
/// <param name="ModelId">永続化とProvider解決に利用する安定ID</param>
/// <param name="DisplayName">ユーザー向け表示名</param>
public sealed record TranscriptionModelDescriptor(TranscriptionModelId ModelId, string DisplayName);

/// <summary>
/// モデル配置状態の確認結果を表す
/// </summary>
/// <param name="State">確認した配置状態</param>
/// <param name="Level">実施した検証レベル</param>
public sealed record TranscriptionModelInspection(
    TranscriptionModelPackageState State,
    TranscriptionModelInspectionLevel Level);

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
