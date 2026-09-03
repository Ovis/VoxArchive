namespace VoxArchive.Wpf;

/// <summary>
/// 音声区間内で発話が存在する時間範囲を表す
/// </summary>
public sealed record SpeechRegion(TimeSpan Start, TimeSpan End)
{
    /// <summary>
    /// 発話区間の長さを取得する
    /// </summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// 文字起こしで認識された1区間のテキストと時間情報を保持する
/// </summary>
public sealed record TranscribedSegment(TimeSpan Start, TimeSpan End, string Text, string? SpeakerLabel = null);
