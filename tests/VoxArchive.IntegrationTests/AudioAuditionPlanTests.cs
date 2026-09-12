using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioAuditionPlanTests
{
    [Test]
    public void ForCutOriginal_UsesExactCutAndOriginalTimeline()
    {
        var cut = new AudioCutRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        var plan = AudioAuditionPlan.ForCutOriginal(cut);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Kind, Is.EqualTo(AudioAuditionKind.CutOriginal));
            Assert.That(plan.SourceStart, Is.EqualTo(cut.Start));
            Assert.That(plan.SourceEnd, Is.EqualTo(cut.End));
            Assert.That(plan.UsesEditedTimeline, Is.False);
        });
    }

    [Test]
    public void ForCutBoundary_UsesThreeSecondContextAndEditedTimeline()
    {
        var cut = new AudioCutRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        var plan = AudioAuditionPlan.ForCutBoundary(cut, TimeSpan.FromSeconds(40));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Kind, Is.EqualTo(AudioAuditionKind.CutBoundary));
            Assert.That(plan.SourceStart, Is.EqualTo(TimeSpan.FromSeconds(7)));
            Assert.That(plan.SourceEnd, Is.EqualTo(TimeSpan.FromSeconds(23)));
            Assert.That(plan.UsesEditedTimeline, Is.True);
        });
    }

    [Test]
    public void ForCutBoundary_ClampsAtSourceEdges()
    {
        var head = AudioAuditionPlan.ForCutBoundary(
            new AudioCutRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(10));
        var tail = AudioAuditionPlan.ForCutBoundary(
            new AudioCutRange(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9.5)),
            TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(head.SourceStart, Is.EqualTo(TimeSpan.Zero));
            Assert.That(head.SourceEnd, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(tail.SourceStart, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(tail.SourceEnd, Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ForSelection_PreservesRequestedPreviewMode(bool edited)
    {
        var plan = AudioAuditionPlan.ForSelection(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(8),
            edited);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Kind, Is.EqualTo(AudioAuditionKind.Selection));
            Assert.That(plan.SourceDuration, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(plan.UsesEditedTimeline, Is.EqualTo(edited));
        });
    }
}
