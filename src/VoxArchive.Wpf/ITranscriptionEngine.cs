namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしエンジンが実装する最小限の実行契約を定義する
/// </summary>
/// <remarks>
/// QueueやOrchestratorがWhisper固有実装へ依存しないための境界である。
/// 現段階では既存設定との互換性を維持するためRequest自体はWhisper向け設定を含むが、
/// 後続の複数エンジン対応でRequestを一般化しても呼び出し側の構造を変えずに済むようにする。
/// </remarks>
public interface ITranscriptionEngine
{
    /// <summary>
    /// 文字起こしを実行する
    /// </summary>
    Task<TranscriptionJobResult> TranscribeAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken = default);
}
