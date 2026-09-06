using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// WhisperがCommon VADを利用する方針をカプセル化する
/// </summary>
public sealed class WhisperSpeechRegionStrategy(ISpeechRegionDetector speechRegionDetector)
{
    /// <summary>
    /// Whisperで認識する発話区間を取得する
    /// </summary>
    public Task<IReadOnlyList<SpeechRegion>> GetRegionsAsync(
        IPreparedTranscriptionAudio audio,
        CancellationToken cancellationToken = default)
        => speechRegionDetector.DetectAsync(audio, cancellationToken);
}
