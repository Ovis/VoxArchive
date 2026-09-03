namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしジョブの実行をエンジン解決へ橋渡しする
/// </summary>
/// <remarks>
/// Queueはスケジューリングと状態管理だけを担当し、どのASR実装を利用するかは本クラスより下へ委譲する。
/// 後続PRで共通の音声準備・VAD・話者判定・Document保存を統括する際も、Queueの責務を増やさずここを拡張する。
/// </remarks>
public sealed class TranscriptionOrchestrator(ITranscriptionEngineResolver engineResolver)
{
    /// <summary>
    /// Requestに対応するエンジンを解決して文字起こしを実行する
    /// </summary>
    public Task<TranscriptionJobResult> TranscribeAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var engine = engineResolver.Resolve(request);
        return engine.TranscribeAsync(request, cancellationToken);
    }
}
