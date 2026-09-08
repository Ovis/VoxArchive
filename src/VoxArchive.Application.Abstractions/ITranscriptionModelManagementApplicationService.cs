namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFの終了処理からASRモデルの再確認・削除状態を監視するFacadeを定義する
/// </summary>
/// <remarks>
/// downloadは既存のITranscriptionApplicationServiceがキャンセル可能操作として扱う。
/// このFacadeはnative validationや削除など強制停止しない操作だけを公開し、終了時は安全な完了点まで待機する。
/// </remarks>
public interface ITranscriptionModelManagementApplicationService
{
    /// <summary>現在実行中の強制停止しないASRモデル操作を取得する</summary>
    TranscriptionModelManagementOperationInfo? GetActiveOperation();

    /// <summary>現在実行中のASRモデル再確認・削除があれば完了まで待つ</summary>
    Task WaitForActiveOperationAsync();
}

/// <summary>終了時に完了待機するASRモデル操作をUIへ公開する</summary>
public sealed record TranscriptionModelManagementOperationInfo(
    string EngineId,
    string ModelId,
    string OperationName);
