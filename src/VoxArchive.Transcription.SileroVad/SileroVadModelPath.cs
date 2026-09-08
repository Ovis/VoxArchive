namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Silero VADモデルのVoxArchive内での物理配置規則を提供する
/// </summary>
public static class SileroVadModelPath
{
    /// <summary>
    /// 既定のSilero VADモデルファイルパスを取得する
    /// </summary>
    /// <remarks>
    /// モデル配置はユーザー設定ではなくアプリ管理状態として扱うため、Whisper等と同じLocalApplicationData配下へ固定する。
    /// 後続のモデル取得・削除処理もこの規則を共有し、UI設定へ物理パスを露出させない。
    /// </remarks>
    public static string GetDefault()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxArchive",
            "models",
            "silero-vad",
            "silero_vad.onnx");
}
