using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// 前処理済み音声から認識対象となる発話区間を検出する
/// </summary>
public interface ISpeechRegionDetector
{
    /// <summary>
    /// 発話区間を検出する
    /// </summary>
    /// <param name="audio">Common側が所有する前処理済み音声</param>
    /// <param name="cancellationToken">処理のキャンセルを通知するトークン</param>
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        CancellationToken cancellationToken = default);
}
