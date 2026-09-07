namespace VoxArchive.Transcription;

/// <summary>
/// 音声内で認識対象とする発話区間を表す
/// </summary>
public sealed record SpeechRegion(TimeSpan Start, TimeSpan End)
{
    /// <summary>
    /// 発話区間の長さを取得する
    /// </summary>
    public TimeSpan Duration => End - Start;
}
