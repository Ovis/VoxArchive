using VoxArchive.Transcription;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Whisper.netの相対timestampをVAD区間へ正規化する境界処理を確認する
/// </summary>
public sealed class WhisperRecognizerTimelineTests
{
    [Test]
    public void NormalizeSegmentTimeline_EndExceedsVadRegion_ClampsToRegionEnd()
    {
        var region = new SpeechRegion(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(9.5),
            TimeSpan.FromSeconds(10.5),
            region,
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
        var region = new SpeechRegion(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromMilliseconds(-200),
            TimeSpan.FromMilliseconds(500),
            region,
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
        var region = new SpeechRegion(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(21));

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            region,
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
        var region = new SpeechRegion(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        var result = WhisperRecognizer.NormalizeSegmentTimeline(
            TimeSpan.FromSeconds(11),
            TimeSpan.FromSeconds(10.5),
            region,
            TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(result.Start, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(result.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }
}
