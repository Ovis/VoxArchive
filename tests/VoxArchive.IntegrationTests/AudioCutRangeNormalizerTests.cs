using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioCutRangeNormalizerTests
{
    [Test]
    public void Constructor_NegativeStart_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new AudioCutRange(TimeSpan.FromTicks(-1), TimeSpan.Zero));
    }

    [TestCase(10, 10)]
    [TestCase(20, 10)]
    public void Constructor_EndNotAfterStart_Throws(long startTicks, long endTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new AudioCutRange(TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(endTicks)));
    }

    [Test]
    public void Normalize_Empty_ReturnsEmpty()
    {
        var actual = AudioCutRangeNormalizer.Normalize(Array.Empty<AudioCutRange>());

        Assert.That(actual, Is.Empty);
    }

    [Test]
    public void Normalize_UnorderedRanges_SortsByStart()
    {
        var actual = AudioCutRangeNormalizer.Normalize(
        [
            Range(30, 40),
            Range(10, 20)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Has.Count.EqualTo(2));
            Assert.That(actual[0], Is.EqualTo(Range(10, 20)));
            Assert.That(actual[1], Is.EqualTo(Range(30, 40)));
        });
    }

    [Test]
    public void Normalize_OverlappingRanges_MergesThem()
    {
        var actual = AudioCutRangeNormalizer.Normalize(
        [
            Range(10, 20),
            Range(15, 30)
        ]);

        Assert.That(actual, Is.EqualTo(new[] { Range(10, 30) }));
    }

    [Test]
    public void Normalize_AdjacentRanges_MergesThem()
    {
        var actual = AudioCutRangeNormalizer.Normalize(
        [
            Range(10, 20),
            Range(20, 30)
        ]);

        Assert.That(actual, Is.EqualTo(new[] { Range(10, 30) }));
    }

    [Test]
    public void Normalize_ChainedRanges_MergesWholeChain()
    {
        var actual = AudioCutRangeNormalizer.Normalize(
        [
            Range(30, 40),
            Range(10, 20),
            Range(18, 30)
        ]);

        Assert.That(actual, Is.EqualTo(new[] { Range(10, 40) }));
    }

    [Test]
    public void Normalize_RangesWithGap_LeavesThemSeparate()
    {
        var actual = AudioCutRangeNormalizer.Normalize(
        [
            Range(10, 20),
            Range(21, 30)
        ]);

        Assert.That(actual, Is.EqualTo(new[] { Range(10, 20), Range(21, 30) }));
    }

    [Test]
    public void Normalize_SubMillisecondPrecision_PreservesOriginalTicks()
    {
        var first = new AudioCutRange(TimeSpan.FromTicks(1), TimeSpan.FromTicks(101));
        var second = new AudioCutRange(TimeSpan.FromTicks(101), TimeSpan.FromTicks(357));

        var actual = AudioCutRangeNormalizer.Normalize([first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Has.Count.EqualTo(1));
            Assert.That(actual[0].Start.Ticks, Is.EqualTo(1));
            Assert.That(actual[0].End.Ticks, Is.EqualTo(357));
        });
    }

    private static AudioCutRange Range(long startTicks, long endTicks)
        => new(TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(endTicks));
}
