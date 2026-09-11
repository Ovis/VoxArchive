using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioEditStateTests
{
    [Test]
    public void Constructor_CutRangesAreNormalizedAndDurationIsReduced()
    {
        var state = new AudioEditState(
            TimeSpan.FromSeconds(30),
            2,
            [Range(10, 20), Range(15, 25)]);

        Assert.Multiple(() =>
        {
            Assert.That(state.CutRanges, Is.EqualTo(new[] { Range(10, 25) }));
            Assert.That(state.EstimatedOutputDuration, Is.EqualTo(TimeSpan.FromSeconds(15)));
        });
    }

    [Test]
    public void Constructor_CutOutsideSource_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new AudioEditState(TimeSpan.FromSeconds(10), 1, [Range(9, 11)]));
    }

    [Test]
    public void AddCutRange_AdjacentRangeMerges()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(30), 1, [Range(5, 10)]);

        var actual = state.AddCutRange(Range(10, 15));

        Assert.That(actual.CutRanges, Is.EqualTo(new[] { Range(5, 15) }));
    }

    [Test]
    public void AllSourceCut_HasNoOutputAudio()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(10), 1, [Range(0, 10)]);

        Assert.Multiple(() =>
        {
            Assert.That(state.EstimatedOutputDuration, Is.EqualTo(TimeSpan.Zero));
            Assert.That(state.HasOutputAudio, Is.False);
        });
    }

    [Test]
    public void AllChannelsMuted_HasNoOutputAudio()
    {
        var muted = new AudioChannelEditState(0d, true);
        var state = new AudioEditState(TimeSpan.FromSeconds(10), 2, channelStates: [muted, muted]);

        Assert.That(state.HasOutputAudio, Is.False);
    }

    [Test]
    public void NegativeInfinityGain_IsAllowedAndConvertsToZero()
    {
        var channel = new AudioChannelEditState(double.NegativeInfinity);

        Assert.That(channel.ToLinearGain(), Is.Zero);
    }

    [Test]
    public void GainAboveTwentyDb_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AudioChannelEditState(20.1d));
    }

    [Test]
    public void EqualStates_WithSeparateArrays_AreStructurallyEqual()
    {
        var first = new AudioEditState(TimeSpan.FromSeconds(30), 2, [Range(5, 10)]);
        var second = new AudioEditState(TimeSpan.FromSeconds(30), 2, [Range(5, 10)]);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
    }

    private static AudioCutRange Range(double startSeconds, double endSeconds)
        => new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));
}
