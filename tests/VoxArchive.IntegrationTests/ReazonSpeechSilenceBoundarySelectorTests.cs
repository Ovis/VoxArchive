using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2向け通常無音分割が15～25秒の探索条件と200ms連続低音量条件を満たすことを確認する
/// </summary>
public sealed class ReazonSpeechSilenceBoundarySelectorTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void Select_ChoosesSilenceWhoseMidpointIsClosestToTwentyFiveSeconds()
    {
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(18.0d, 18.3d, 0.10d),
            CreateLowVolumeFrames(24.4d, 24.8d, 0.10d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.SilenceStartSample, Is.EqualTo(Samples(24.4d)));
            Assert.That(selected.SilenceEndSample, Is.EqualTo(Samples(24.8d)));
            Assert.That(selected.SelectedSample, Is.EqualTo(Samples(24.6d)));
            Assert.That(selected.TargetSample, Is.EqualTo(Samples(25d)));
            Assert.That(selected.SearchStartSample, Is.EqualTo(Samples(15d)));
            Assert.That(selected.SearchEndSample, Is.EqualTo(Samples(25d)));
        });
    }

    [Test]
    public void Select_MergesOverlappingLowVolumeFramesBeforeCheckingTwoHundredMilliseconds()
    {
        // 30ms window / 10ms hopでは各frame単体は200ms未満だが、連続する低音量frameの覆うsample区間は200ms以上になる。
        var frames = new List<ReazonSpeechRmsFrame>();
        for (var start = Samples(24.50d); start <= Samples(24.68d); start += 160)
        {
            frames.Add(new ReazonSpeechRmsFrame(start, start + 480, 0.10d));
        }
        var analysis = new ReazonSpeechRmsAnalysis(frames, 0.20d);

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
        Assert.That(
            selected!.SilenceEndSample - selected.SilenceStartSample,
            Is.GreaterThanOrEqualTo(ReazonSpeechSilenceBoundarySelector.MinimumSilenceSamples));
    }

    [Test]
    public void Select_IgnoresContinuousLowVolumeIntervalShorterThanTwoHundredMilliseconds()
    {
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(24.70d, 24.85d, 0.10d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Null);
    }

    [Test]
    public void Select_IgnoresLowVolumeFramesOutsideFifteenToTwentyFiveSecondSearchWindow()
    {
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(14.0d, 14.5d, 0.10d),
            CreateLowVolumeFrames(25.1d, 25.5d, 0.10d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Null);
    }

    [Test]
    public void Select_TreatsRmsEqualToP20AsLowVolume()
    {
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(24.5d, 24.8d, 0.20d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
    }

    [Test]
    public void Select_RejectsBoundaryThatWouldLeaveTailShorterThanThreeSeconds()
    {
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(15.0d, 15.4d, 0.10d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(17.5d));

        Assert.That(selected, Is.Null);
    }

    [Test]
    public void Select_UsesChunkStartAsOriginForSearchWindow()
    {
        var chunkStart = Samples(30d);
        var analysis = CreateAnalysis(
            p20: 0.20d,
            CreateLowVolumeFrames(54.4d, 54.8d, 0.10d));

        var selected = ReazonSpeechSilenceBoundarySelector.Select(
            analysis,
            chunkStart,
            regionEndSample: Samples(70d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.SelectedSample, Is.EqualTo(Samples(54.6d)));
            Assert.That(selected.SearchStartSample, Is.EqualTo(Samples(45d)));
            Assert.That(selected.TargetSample, Is.EqualTo(Samples(55d)));
        });
    }

    private static ReazonSpeechRmsAnalysis CreateAnalysis(
        double p20,
        params IReadOnlyList<ReazonSpeechRmsFrame>[] groups)
        => new(groups.SelectMany(x => x).OrderBy(x => x.StartSample).ToArray(), p20);

    private static IReadOnlyList<ReazonSpeechRmsFrame> CreateLowVolumeFrames(
        double startSeconds,
        double endSeconds,
        double rms)
    {
        var frames = new List<ReazonSpeechRmsFrame>();
        var start = Samples(startSeconds);
        var lastStart = Samples(endSeconds) - 480;
        for (var current = start; current <= lastStart; current += 160)
        {
            frames.Add(new ReazonSpeechRmsFrame(current, current + 480, rms));
        }
        return frames;
    }

    private static long Samples(double seconds) => (long)Math.Round(seconds * SampleRate);
}
