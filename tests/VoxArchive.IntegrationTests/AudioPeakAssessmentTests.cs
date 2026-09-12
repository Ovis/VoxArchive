using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioPeakAssessmentTests
{
    [Test]
    public void FromPeak_BelowTarget_DoesNotAttenuate()
    {
        var result = AudioPeakAssessment.FromPeak(0.5d);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsClipping, Is.False);
            Assert.That(result.PeakDbfs, Is.EqualTo(-6.0206d).Within(0.001));
            Assert.That(result.RequiredMasterGainDb, Is.Zero);
        });
    }

    [Test]
    public void FromPeak_AboveZeroDbfs_CalculatesMinusPointOneTarget()
    {
        var result = AudioPeakAssessment.FromPeak(1.25d);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsClipping, Is.True);
            Assert.That(result.PeakDbfs, Is.EqualTo(1.9382d).Within(0.001));
            Assert.That(result.TargetPeakDbfs, Is.EqualTo(-0.1d));
            Assert.That(result.RequiredMasterGainDb, Is.EqualTo(-2.0382d).Within(0.001));
        });
    }

    [Test]
    public void FromPeak_BetweenTargetAndZero_RequestsSmallSafetyAttenuationWithoutClipping()
    {
        var result = AudioPeakAssessment.FromPeak(1d);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsClipping, Is.False);
            Assert.That(result.PeakDbfs, Is.Zero.Within(0.0001));
            Assert.That(result.RequiredMasterGainDb, Is.EqualTo(-0.1d).Within(0.0001));
        });
    }
}
