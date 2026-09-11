using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioWaveformViewportTests
{
    [Test]
    public void ZoomAround_KeepsAnchorRatioAndBounds()
    {
        var source = TimeSpan.FromSeconds(100);
        var viewport = AudioWaveformViewport.Full(source);

        var zoomed = viewport.ZoomAround(TimeSpan.FromSeconds(25), 2d, source, TimeSpan.FromMilliseconds(100));

        Assert.Multiple(() =>
        {
            Assert.That(zoomed.Duration.TotalSeconds, Is.EqualTo(50d).Within(0.001));
            Assert.That(zoomed.Start.TotalSeconds, Is.EqualTo(12.5d).Within(0.001));
            Assert.That(zoomed.End.TotalSeconds, Is.EqualTo(62.5d).Within(0.001));
        });
    }

    [Test]
    public void ScrollBy_ClampsAtSourceEdgesWithoutChangingDuration()
    {
        var source = TimeSpan.FromSeconds(100);
        var viewport = AudioWaveformViewport.Normalize(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), source);

        var left = viewport.ScrollBy(TimeSpan.FromSeconds(-50), source);
        var right = viewport.ScrollBy(TimeSpan.FromSeconds(100), source);

        Assert.Multiple(() =>
        {
            Assert.That(left.Start, Is.EqualTo(TimeSpan.Zero));
            Assert.That(left.End, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(right.Start, Is.EqualTo(TimeSpan.FromSeconds(80)));
            Assert.That(right.End, Is.EqualTo(source));
        });
    }

    [Test]
    public void EnsureVisible_MovesOnlyWhenPlayheadIsOutsideViewport()
    {
        var source = TimeSpan.FromSeconds(100);
        var viewport = AudioWaveformViewport.Normalize(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), source);

        var unchanged = viewport.EnsureVisible(TimeSpan.FromSeconds(30), source);
        var moved = viewport.EnsureVisible(TimeSpan.FromSeconds(70), source);

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.EqualTo(viewport));
            Assert.That(moved.Start.TotalSeconds, Is.EqualTo(60d).Within(0.001));
            Assert.That(moved.End.TotalSeconds, Is.EqualTo(80d).Within(0.001));
        });
    }

    [Test]
    public void PageForward_KeepsSmallOverlap()
    {
        var source = TimeSpan.FromSeconds(100);
        var viewport = AudioWaveformViewport.Normalize(TimeSpan.Zero, TimeSpan.FromSeconds(20), source);

        var next = viewport.PageForward(source, 0.1d);

        Assert.Multiple(() =>
        {
            Assert.That(next.Start.TotalSeconds, Is.EqualTo(18d).Within(0.001));
            Assert.That(next.End.TotalSeconds, Is.EqualTo(38d).Within(0.001));
        });
    }
}
