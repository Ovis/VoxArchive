namespace VoxArchive.Application.Abstractions;

/// <summary>
/// WPFから共通発話検出モデルの状態確認・取得・削除を操作するFacadeを定義する
/// </summary>
/// <remarks>
/// Silero固有型をPresentationへ漏らさず、UIに必要な状態と進捗だけを公開する。
/// ASRモデル管理とは責務が異なるためITranscriptionApplicationServiceへ混在させない。
/// </remarks>
public interface ISpeechRegionDetectorModelApplicationService
{
    /// <summary>現在の発話検出モデル状態を取得する</summary>
    SpeechRegionDetectorModelStatusInfo Inspect();

    /// <summary>配置済みモデルを実ロードして状態を再確認する</summary>
    Task<SpeechRegionDetectorModelStatusInfo> ReverifyAsync(CancellationToken cancellationToken = default);

    /// <summary>発話検出モデルを安全なtransactionで取得・検証する</summary>
    Task InstallAsync(
        bool force,
        IProgress<SpeechRegionDetectorModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>発話検出モデルを安全に削除する</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>発話検出モデルの検査状態と実行可能性をUIへ公開する</summary>
public sealed record SpeechRegionDetectorModelStatusInfo(string State, bool IsReady);

/// <summary>
/// 発話検出モデル取得の進捗をUIへ公開する
/// </summary>
/// <remarks>
/// 配布元が総容量を提供しない場合はTotalBytes/Percentをnullのまま返し、UIが不定進捗として表示できるようにする。
/// native load validation中はCurrentFileNameをnull、IsValidatingをtrueとして通知する。
/// </remarks>
public sealed record SpeechRegionDetectorModelTransferInfo(
    long BytesReceived,
    long? TotalBytes,
    string? CurrentFileName,
    bool IsValidating)
{
    /// <summary>総容量が既知の場合だけ0～100の進捗率を返す</summary>
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}
