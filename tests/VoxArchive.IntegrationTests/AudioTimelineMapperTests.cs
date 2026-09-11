using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioTimelineMapperTests
{
    [Test]
    public void ResolveSourceSeekTarget_CutRangeInside_UsesDirectionBoundary()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(30), 2)
            .AddCutRange(new AudioCutRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)));
        var mapper = new AudioTimelineMapper(state, 48_000);

        Assert.Multiple(() =>
        {
            Assert.That(mapper.ResolveSourceSeekTarget(TimeSpan.FromSeconds(15)), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(mapper.ResolveSourceSeekTarget(TimeSpan.FromSeconds(15), AudioSeekDirection.Backward), Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
    }

    [Test]
    public void SourceToRendered_AccountsForCutAndCrossfade()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(30), 2)
            .AddCutRange(new AudioCutRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)));
        var mapper = new AudioTimelineMapper(state, 48_000);

        Assert.Multiple(() =>
        {
            Assert.That(mapper.RenderedDuration.TotalSeconds, Is.EqualTo(19.995d).Within(0.0001));
            Assert.That(mapper.SourceToRendered(TimeSpan.FromSeconds(20)).TotalSeconds, Is.EqualTo(9.995d).Within(0.0001));
            Assert.That(mapper.SourceToRendered(TimeSpan.FromSeconds(25)).TotalSeconds, Is.EqualTo(14.995d).Within(0.0001));
        });
    }

    [Test]
    public void RenderedToSource_JumpsOverCutRange()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(30), 2)
            .AddCutRange(new AudioCutRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)));
        var mapper = new AudioTimelineMapper(state, 48_000);

        var justBeforeJunction = mapper.RenderedToSource(TimeSpan.FromSeconds(9.994));
        var junction = mapper.RenderedToSource(TimeSpan.FromSeconds(9.996));
        var later = mapper.RenderedToSource(TimeSpan.FromSeconds(15));

        Assert.Multiple(() =>
        {
            Assert.That(justBeforeJunction.TotalSeconds, Is.LessThan(10d));
            Assert.That(junction.TotalSeconds, Is.GreaterThanOrEqualTo(20d));
            Assert.That(later.TotalSeconds, Is.EqualTo(25.005d).Within(0.002));
        });
    }

    [Test]
    public void NoCuts_MappingIsIdentity()
    {
        var state = new AudioEditState(TimeSpan.FromSeconds(30), 1);
        var mapper = new AudioTimelineMapper(state, 44_100);
        var position = TimeSpan.FromSeconds(12.345);

        Assert.Multiple(() =>
        {
            Assert.That(mapper.SourceToRendered(position).TotalSeconds, Is.EqualTo(position.TotalSeconds).Within(1d / 44_100));
            Assert.That(mapper.RenderedToSource(position).TotalSeconds, Is.EqualTo(position.TotalSeconds).Within(1d / 44_100));
        });
    }
}
