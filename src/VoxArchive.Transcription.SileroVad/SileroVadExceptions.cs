namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Silero VADモデルをロードできず、推論を開始できなかったことを表す
/// </summary>
/// <remarks>
/// モデル未配置・破損・native初期化失敗は推論失敗とは区別する。
/// セッション内の「3回連続推論失敗」へ含めないため、選択detectorがこの例外を判別する。
/// </remarks>
internal sealed class SileroVadUnavailableException : Exception
{
    /// <summary>原因例外を保持して初期化する</summary>
    internal SileroVadUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
