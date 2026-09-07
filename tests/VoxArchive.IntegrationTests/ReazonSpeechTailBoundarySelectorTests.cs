using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2の短い末尾再配分が概ね等分となる境界を選び、無音候補を優先することを確認する
/// </summary>
public sealed class ReazonSpeechTailBoundarySelectorTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void Select_WhenCombinedRangeFitsWithinTwentyFiveSeconds_ReturnsNullForMerge()
    {
        var analysis = CreateAnalysis(Frame(10d, 0.10d));

        var selected = ReazonSpeechTailBoundarySelector.Select(
            analysis,
            combinedStartSample: Samples(0d),
            regionEndSample: Samples(24d));

        Assert.That(selected, Is.Null);
    }

    [Test]
    public void Select_PrefersTwoHundredMillisecondSilenceNearEqualSplitTarget()
    {
        var frames = new List<ReazonSpeechRmsFrame>();
        frames.AddRange(CreateContinuousFrames(12.8d, 13.2d, 0.10d));
        frames.Add(Frame(13.5d, 0.01d));
        var analysis = new ReazonSpeechRmsAnalysis(frames.OrderBy(x => x.StartSample).ToArray(), P20Rms: 0.20d);

        var selected = ReazonSpeechTailBoundarySelector.Select(
            analysis,
            combinedStartSample: Samples(0d),
            regionEndSample: Samples(26d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.UsedSilence, Is.True);
            Assert.That(selected.SelectedSample, Is.EqualTo(Samples(13d)));
            Assert.That(selected.TargetSample, Is.EqualTo(Samples(13d)));
        });
    }

    [Test]
    public void Select_WhenNoSilenceExists_ChoosesMinimumRmsFrameWithinTargetPlusMinusTwoSeconds()
    {
        var analysis = CreateAnalysis(
            Frame(10.0d, 0.01d),
            Frame(12.0d, 0.20d),
            Frame(13.4d, 0.05d),
            Frame(14.5d, 0.10d));

        var selected = ReazonSpeechTailBoundarySelector.Select(
            analysis,
            combinedStartSample: Samples(0d),
            regionEndSample: Samples(26d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.UsedSilence, Is.False);
            Assert.That(selected.SelectedSample, Is.EqualTo(Samples(13.415d)));
            Assert.That(selected.Rms, Is.EqualTo(0.05d));
            Assert.That(selected.SearchStartSample, Is.EqualTo(Samples(11d)));
            Assert.That(selected.SearchEndSample, Is.EqualTo(Samples(15d)));
        });
    }

    [Test]
    public void Select_ClampsSearchWindowSoBothResultingChunksRemainAtLeastThreeSeconds()
    {
        var analysis = CreateAnalysis(
            Frame(2.5d, 0.01d),
            Frame(3.2d, 0.10d),
            Frame(22.8d, 0.10d),
            Frame(23.5d, 0.01d));

        var selected = ReazonSpeechTailBoundarySelector.Select(
            analysis,
            combinedStartSample: Samples(0d),
            regionEndSample: Samples(26d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.SelectedSample, Is.GreaterThanOrEqualTo(Samples(3d)));
            Assert.That(selected.SelectedSample, Is.LessThanOrEqualTo(Samples(23d)));
        });
    }

    [Test]
    public void Select_UsesCombinedStartAsEqualSplitOrigin()
    {
        var analysis = CreateAnalysis(
            Frame(42.0d, 0.20d),
            Frame(43.4d, 0.05d));

        var selected = ReazonSpeechTailBoundarySelector.Select(
            analysis,
            combinedStartSample: Samples(30d),
            regionEndSample: Samples(56d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.TargetSample, Is.EqualTo(Samples(43d)));
            Assert.That(selected.SelectedSample, Is.EqualTo(Samples(43.415d)));
        });
    }

    private static ReazonSpeechRmsAnalysis CreateAnalysis(params ReazonSpeechRmsFrame[] frames)
        => new(frames.OrderBy(x => x.StartSample).ToArray(), P20Rms: 0.10d);

    private static IReadOnlyList<ReazonSpeechRmsFrame> CreateContinuousFrames(
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

    private static ReazonSpeechRmsFrame Frame(double startSeconds, double rms)
    {
        var start = Samples(startSeconds);
        return new ReazonSpeechRmsFrame(start, start + 480, rms);
    }

    private static long Samples(double seconds) => (long)Math.Round(seconds * SampleRate);
}
