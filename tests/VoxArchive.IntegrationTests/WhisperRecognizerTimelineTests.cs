using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Whisper.netの相対timestampをVAD区間へ正規化する境界処理を確認する
/// </summary>
public sealed class WhisperRecognizerTimelineTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void NormalizeSegmentTimeline_EndExceedsVadRegion_ClampsToRegionEnd()
    {
        var region = CreateRegion(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(9.5),
            TimeSpan.FromSeconds(10.5),
            region,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(19.5)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_NegativeStart_ClampsToRegionStart()
    {
        var region = CreateRegion(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromMilliseconds(-200),
            TimeSpan.FromMilliseconds(500),
            region,
            SampleRate,
            TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(10.5)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_RegionExceedsAudio_ClampsToAudioDuration()
    {
        var region = CreateRegion(18, 21);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            region,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(19)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    [Test]
    public void NormalizeSegmentTimeline_StartBeyondRegion_EndDoesNotBecomeEarlierThanStart()
    {
        var region = CreateRegion(10, 20);

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(11),
            TimeSpan.FromSeconds(10.5),
            region,
            SampleRate,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    private static SpeechRegion CreateRegion(double startSeconds, double endSeconds)
    {
        var start = (long)(startSeconds * SampleRate);
        var end = (long)(endSeconds * SampleRate);
        return new SpeechRegion(
            0,
            start,
            end,
            [new AudioSampleRange(start, end)],
            [0]);
    }
}
