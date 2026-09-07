namespace VoxArchive.Application.Abstractions;

/// <summary>
/// 手動文字起こしに伴うモデル取得の進捗表示をPresentation層へ委譲するUI portを定義する
/// </summary>
/// <remarks>
/// モデル取得処理そのものはApplicationが所有する。
/// Presentation層は進捗DTOの表示と、Applicationから渡されたキャンセル操作の受付だけを担当する。
/// </remarks>
public interface ITranscriptionModelDownloadProgressPresentation
{
    /// <summary>
    /// モデル取得の進捗表示を開始する
    /// </summary>
    /// <param name="model">取得対象モデル</param>
    /// <param name="cancelAction">利用者が明示的に取得を中止したときに呼び出すApplication所有の操作</param>
    ITranscriptionModelDownloadProgressSession Show(
        TranscriptionMissingModelInfo model,
        Action cancelAction);
}

/// <summary>
/// 1回のモデル取得に対応する進捗表示Sessionを定義する
/// </summary>
public interface ITranscriptionModelDownloadProgressSession : IDisposable
{
    /// <summary>最新の取得進捗を表示へ反映する</summary>
    void Report(TranscriptionModelTransferInfo progress);

    /// <summary>モデル取得処理の終了に合わせて進捗表示を閉じる</summary>
    void CloseAfterCompletion();
}
