namespace VoxArchive.Wpf;

/// <summary>
/// 旧canonical resultをLibrary UIで表示・派生出力する際に使用する表示用segmentを保持する
/// </summary>
/// <remarks>
/// 文字認識Engineの実行契約ではない。旧Library readerをApplication側のresult Use Caseへ移すまでの
/// Presentation互換DTOとして限定して残し、Engine/Common実装との依存を再導入しない。
/// </remarks>
public sealed record TranscribedSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? SpeakerLabel = null);
