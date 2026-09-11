using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioRenderingPipelineTests
{
    [Test]
    public void RenderPlan_CutRangesBecomeKeepRangesOnOriginalTimeline()
    {
        var state = new AudioEditState(
            TimeSpan.FromSeconds(10),
            2,
            [Range(2, 4), Range(6, 7)]);

        var plan = AudioRenderPlan.Create(state, 1000);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SourceFrameCount, Is.EqualTo(10_000));
            Assert.That(plan.KeepRanges, Is.EqualTo(new[]
            {
                new AudioFrameRange(0, 2000),
                new AudioFrameRange(4000, 6000),
                new AudioFrameRange(7000, 10_000)
            }));
            Assert.That(plan.KeptFrameCount, Is.EqualTo(7000));
            Assert.That(plan.Junctions, Has.Count.EqualTo(2));
            Assert.That(plan.Junctions.All(x => x.CrossfadeFrameCount == 5), Is.True);
        });
    }

    [Test]
    public void RenderPlan_FiveMillisecondCrossfadeIsBoundedByShorterSide()
    {
        var state = new AudioEditState(
            TimeSpan.FromMilliseconds(20),
            1,
            [new AudioCutRange(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(19))]);

        var plan = AudioRenderPlan.Create(state, 1000);

        Assert.Multiple(() =>
        {
            Assert.That(plan.KeepRanges, Is.EqualTo(new[]
            {
                new AudioFrameRange(0, 2),
                new AudioFrameRange(19, 20)
            }));
            Assert.That(plan.Junctions.Single().CrossfadeFrameCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void RenderPlan_CutAtBeginningAndEndLeavesOnlyMiddle()
    {
        var state = new AudioEditState(
            TimeSpan.FromSeconds(10),
            1,
            [Range(0, 2), Range(8, 10)]);

        var plan = AudioRenderPlan.Create(state, 1000);

        Assert.That(plan.KeepRanges, Is.EqualTo(new[] { new AudioFrameRange(2000, 8000) }));
        Assert.That(plan.Junctions, Is.Empty);
    }

    [Test]
    public void TimeToFrameIndex_RoundsToNearestFrame()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioRenderPlan.TimeToFrameIndex(TimeSpan.FromTicks(4_999), 1000), Is.EqualTo(0));
            Assert.That(AudioRenderPlan.TimeToFrameIndex(TimeSpan.FromTicks(5_000), 1000), Is.EqualTo(1));
            Assert.That(AudioRenderPlan.TimeToFrameIndex(TimeSpan.FromTicks(15_000), 1000), Is.EqualTo(2));
        });
    }

    [Test]
    public void FrameProcessor_StereoAppliesIndependentGainAndMuteWithoutClipping()
    {
        var states = new[]
        {
            new AudioChannelEditState(6.020599913279624d),
            new AudioChannelEditState(0d, true)
        };
        Span<float> output = stackalloc float[2];

        var channels = AudioFrameProcessor.ProcessFrame(
            [0.75f, 0.5f],
            output,
            states,
            AudioRenderChannelMode.Stereo);

        Assert.Multiple(() =>
        {
            Assert.That(channels, Is.EqualTo(2));
            Assert.That(output[0], Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(output[1], Is.Zero);
        });
    }

    [Test]
    public void FrameProcessor_MonoMixdownUsesSumWithoutImplicitHalfGain()
    {
        var states = new[]
        {
            AudioChannelEditState.Default,
            AudioChannelEditState.Default
        };
        Span<float> output = stackalloc float[1];

        AudioFrameProcessor.ProcessFrame(
            [0.8f, 0.8f],
            output,
            states,
            AudioRenderChannelMode.MonoMixdown);

        Assert.That(output[0], Is.EqualTo(1.6f).Within(0.0001f));
    }

    [Test]
    public void FrameProcessor_MasterGainIsAppliedAtFinalStage()
    {
        var states = new[]
        {
            AudioChannelEditState.Default,
            AudioChannelEditState.Default
        };
        Span<float> output = stackalloc float[1];

        AudioFrameProcessor.ProcessFrame(
            [0.5f, 0.5f],
            output,
            states,
            AudioRenderChannelMode.MonoMixdown,
            -6.020599913279624d);

        Assert.That(output[0], Is.EqualTo(0.5f).Within(0.0001f));
    }

    [Test]
    public void FrameProcessor_MonoInputRemainsMonoEvenInStereoMode()
    {
        Span<float> output = stackalloc float[1];

        var channels = AudioFrameProcessor.ProcessFrame(
            [0.25f],
            output,
            [AudioChannelEditState.Default],
            AudioRenderChannelMode.Stereo);

        Assert.Multiple(() =>
        {
            Assert.That(channels, Is.EqualTo(1));
            Assert.That(output[0], Is.EqualTo(0.25f));
        });
    }

    [Test]
    public void ClippingAnalysis_DetectsPostMixPeakAndCalculatesSafeMasterGain()
    {
        var analysis = new AudioClippingAnalysis();
        analysis.Observe([0.25f, -1.6f, 0.9f]);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.PeakAbsoluteSample, Is.EqualTo(1.6d).Within(0.0001d));
            Assert.That(analysis.IsClipping, Is.True);
            Assert.That(analysis.RequiredMasterGainDb, Is.EqualTo(-4.082399653d).Within(0.0001d));
            Assert.That(analysis.GetSafeMasterGainDb(), Is.EqualTo(analysis.RequiredMasterGainDb).Within(0.0001d));
        });
    }

    [Test]
    public void ClippingAnalysis_DoesNotRaiseAlreadyLowerMasterGain()
    {
        var analysis = new AudioClippingAnalysis();
        analysis.Observe([2f]);

        Assert.That(analysis.GetSafeMasterGainDb(-12d), Is.EqualTo(-12d));
    }

    [Test]
    public void ClippingAnalysis_NoClippingPreservesUserMasterGain()
    {
        var analysis = new AudioClippingAnalysis();
        analysis.Observe([0.8f]);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.IsClipping, Is.False);
            Assert.That(analysis.RequiredMasterGainDb, Is.Zero);
            Assert.That(analysis.GetSafeMasterGainDb(-3d), Is.EqualTo(-3d));
        });
    }

    [Test]
    public void CrossfadeMixer_MixesFromLeftTowardRightWithoutClippingStage()
    {
        var left = new[] { 1f, 1f, 1f };
        var right = new[] { 0f, 0f, 0f };
        var destination = new float[3];

        AudioCrossfadeMixer.Mix(left, right, destination, 1);

        Assert.Multiple(() =>
        {
            Assert.That(destination[0], Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(destination[1], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(destination[2], Is.EqualTo(0.25f).Within(0.0001f));
        });
    }

    [Test]
    public void CrossfadeMixer_StereoChannelsAreMixedIndependently()
    {
        var left = new[] { 1f, -1f, 1f, -1f };
        var right = new[] { 0f, 1f, 0f, 1f };
        var destination = new float[4];

        AudioCrossfadeMixer.Mix(left, right, destination, 2);

        Assert.Multiple(() =>
        {
            Assert.That(destination[0], Is.EqualTo(2f / 3f).Within(0.0001f));
            Assert.That(destination[1], Is.EqualTo(-1f / 3f).Within(0.0001f));
            Assert.That(destination[2], Is.EqualTo(1f / 3f).Within(0.0001f));
            Assert.That(destination[3], Is.EqualTo(1f / 3f).Within(0.0001f));
        });
    }

    [Test]
    public void MixdownAndClippingAnalysis_WorkAsOnePipeline()
    {
        var states = new[]
        {
            new AudioChannelEditState(6.020599913279624d),
            AudioChannelEditState.Default
        };
        Span<float> mixed = stackalloc float[1];
        AudioFrameProcessor.ProcessFrame(
            [0.5f, 0.5f],
            mixed,
            states,
            AudioRenderChannelMode.MonoMixdown);

        var analysis = new AudioClippingAnalysis();
        analysis.Observe(mixed);

        Assert.Multiple(() =>
        {
            Assert.That(mixed[0], Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(analysis.IsClipping, Is.True);
            Assert.That(analysis.RequiredMasterGainDb, Is.EqualTo(-3.521825181d).Within(0.0001d));
        });
    }

    private static AudioCutRange Range(double startSeconds, double endSeconds)
        => new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));
}
