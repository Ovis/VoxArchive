using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Whisper.netの相対timestampをRecognitionChunkへ正規化する境界処理を確認する
/// </summary>
public sealed class WhisperRecognizerTimelineTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void NormalizeSegmentTimeline_EndExceedsChunk_ClampsToChunkEnd()
    {
        var chunk = CreateChunk(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(9.5),
            TimeSpan.FromSeconds(10.5),
            chunk,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(19.5)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_NegativeStart_ClampsToChunkStart()
    {
        var chunk = CreateChunk(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromMilliseconds(-200),
            TimeSpan.FromMilliseconds(500),
            chunk,
            SampleRate,
            TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(10.5)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_ChunkExceedsAudio_ClampsToAudioDuration()
    {
        var chunk = CreateChunk(18, 21);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            chunk,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(19)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_StartBeyondChunk_EndDoesNotBecomeEarlierThanStart()
    {
        var chunk = CreateChunk(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(11),
            TimeSpan.FromSeconds(10.5),
            chunk,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    private static RecognitionChunk CreateChunk(double startSeconds, double endSeconds)
        => new(
            0,
            0,
            (long)(startSeconds * SampleRate),
            (long)(endSeconds * SampleRate));
}
