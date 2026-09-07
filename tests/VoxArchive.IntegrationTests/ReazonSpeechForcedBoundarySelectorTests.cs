using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2のforced splitが25秒直前1秒から最小RMS位置を選ぶことを確認する
/// </summary>
public sealed class ReazonSpeechForcedBoundarySelectorTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void Select_ChoosesMinimumRmsFrameWithinLastSecondBeforeTarget()
    {
        var analysis = CreateAnalysis(
            Frame(24.10d, 0.20d),
            Frame(24.40d, 0.05d),
            Frame(24.80d, 0.10d));

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.SelectedSample, Is.EqualTo(Samples(24.415d)));
            Assert.That(selected.Rms, Is.EqualTo(0.05d));
            Assert.That(selected.SearchStartSample, Is.EqualTo(Samples(24d)));
            Assert.That(selected.SearchEndSample, Is.EqualTo(Samples(25d)));
            Assert.That(selected.TargetSample, Is.EqualTo(Samples(25d)));
        });
    }

    [Test]
    public void Select_IgnoresLowerRmsFrameOutsideLastSecondBeforeTarget()
    {
        var analysis = CreateAnalysis(
            Frame(23.50d, 0.01d),
            Frame(24.60d, 0.20d));

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.SelectedSample, Is.EqualTo(Samples(24.615d)));
    }

    [Test]
    public void Select_WhenRmsTies_ChoosesFrameCloserToTwentyFiveSeconds()
    {
        var analysis = CreateAnalysis(
            Frame(24.20d, 0.10d),
            Frame(24.70d, 0.10d));

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.SelectedSample, Is.EqualTo(Samples(24.715d)));
    }

    [Test]
    public void Select_DoesNotRequireFrameToBeBelowP20()
    {
        var analysis = new ReazonSpeechRmsAnalysis(
            [Frame(24.50d, 0.80d)],
            P20Rms: 0.10d);

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(40d));

        // forced splitは通常無音候補がない場合の退避経路なので、P20以下であること自体は条件にしない。
        Assert.That(selected, Is.Not.Null);
    }

    [Test]
    public void Select_RejectsBoundaryThatWouldLeaveTailShorterThanThreeSeconds()
    {
        var analysis = CreateAnalysis(Frame(24.20d, 0.05d));

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStartSample: 0,
            regionEndSample: Samples(26d));

        Assert.That(selected, Is.Null);
    }

    [Test]
    public void Select_UsesCurrentChunkStartAsTargetOrigin()
    {
        var chunkStart = Samples(30d);
        var analysis = CreateAnalysis(
            Frame(54.10d, 0.20d),
            Frame(54.70d, 0.05d));

        var selected = ReazonSpeechForcedBoundarySelector.Select(
            analysis,
            chunkStart,
            regionEndSample: Samples(70d));

        Assert.That(selected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(selected!.SelectedSample, Is.EqualTo(Samples(54.715d)));
            Assert.That(selected.SearchStartSample, Is.EqualTo(Samples(54d)));
            Assert.That(selected.TargetSample, Is.EqualTo(Samples(55d)));
        });
    }

    private static ReazonSpeechRmsAnalysis CreateAnalysis(params ReazonSpeechRmsFrame[] frames)
        => new(frames.OrderBy(x => x.StartSample).ToArray(), P20Rms: 0.10d);

    private static ReazonSpeechRmsFrame Frame(double startSeconds, double rms)
    {
        var start = Samples(startSeconds);
        return new ReazonSpeechRmsFrame(start, start + 480, rms);
    }

    private static long Samples(double seconds) => (long)Math.Round(seconds * SampleRate);
}
