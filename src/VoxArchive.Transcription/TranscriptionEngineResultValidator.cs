using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engineに依存しない認識結果契約を検証する
/// </summary>
public sealed class TranscriptionEngineResultValidator
{
    /// <summary>
    /// Engine結果が元録音基準のabsolute timeline契約を満たすか検証する
    /// </summary>
    /// <param name="result">検証対象のEngine結果</param>
    /// <param name="recordingDuration">元録音の長さ</param>
    public void Validate(TranscriptionEngineResult result, TimeSpan recordingDuration)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (recordingDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recordingDuration));
        }

        TimeSpan? previousStart = null;
        for (var i = 0; i < result.Segments.Count; i++)
        {
            var segment = result.Segments[i]
                ?? throw new InvalidDataException($"Engine result segment[{i}]がnullです。");
            if (segment.Start < TimeSpan.Zero)
            {
                throw new InvalidDataException($"Engine result segment[{i}]のStartが負数です。");
            }
            if (segment.End < segment.Start)
            {
                throw new InvalidDataException($"Engine result segment[{i}]のEndがStartより前です。");
            }
            if (segment.End > recordingDuration)
            {
                // Common側でsilent clampするとEngineのtimelineバグを隠すため、契約違反として失敗させる。
                throw new InvalidDataException($"Engine result segment[{i}]が録音時間を超えています。");
            }
            if (segment.Text is null)
            {
                throw new InvalidDataException($"Engine result segment[{i}]のTextがnullです。");
            }
            if (previousStart is not null && segment.Start < previousStart.Value)
            {
                throw new InvalidDataException($"Engine result segment[{i}]のtimeline順序が不正です。");
            }

            previousStart = segment.Start;
        }
    }
}
