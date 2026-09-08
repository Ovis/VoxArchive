namespace VoxArchive.Transcription;

/// <summary>
/// Common/Applicationから発話検出器のモデル管理を操作するための抽象契約を定義する
/// </summary>
/// <remarks>
/// Silero固有型をApplicationやWPFへ公開するとEngine/VAD実装への依存が上位層へ漏れるため、
/// UIに必要な状態・取得・再確認・削除だけを共通契約へ投影する。
/// </remarks>
public interface ISpeechRegionDetectorModelManager
{
    /// <summary>現在の発話検出モデル状態を取得する</summary>
    SpeechRegionDetectorModelState GetState();

    /// <summary>配置済みモデルを実ロードして状態を再確認する</summary>
    SpeechRegionDetectorModelState Recheck();

    /// <summary>モデルを安全なtransactionで取得・検証する</summary>
    Task InstallAsync(
        bool force,
        IProgress<ManagedModelTransactionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>モデル一式を安全に削除する</summary>
    void Delete();
}

/// <summary>
/// UIへ公開可能な発話検出モデル状態を表す
/// </summary>
public enum SpeechRegionDetectorModelState
{
    /// <summary>モデルが未取得</summary>
    Missing = 0,

    /// <summary>実ロードまで成功して利用可能</summary>
    Available = 1,

    /// <summary>ファイルはあるが実ロードに失敗</summary>
    Unavailable = 2,

    /// <summary>取得後検証または再確認中</summary>
    Checking = 3
}
