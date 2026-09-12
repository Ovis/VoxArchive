using System.Globalization;
using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioGainValueTests
{
    [Test]
    public void SliderMinimum_IsNegativeInfinity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioGainValue.FromSlider(-60d), Is.EqualTo(double.NegativeInfinity));
            Assert.That(AudioGainValue.ToSlider(double.NegativeInfinity), Is.EqualTo(-60d));
            Assert.That(AudioGainValue.FromSlider(-59.5d), Is.EqualTo(-59.5d));
        });
    }

    [TestCase("-∞")]
    [TestCase("-inf")]
    [TestCase("-Infinity")]
    public void TryParse_NegativeInfinityNotation(string text)
    {
        var result = AudioGainValue.TryParse(text, CultureInfo.InvariantCulture, out var value);
        Assert.That(result, Is.True);
        Assert.That(value, Is.EqualTo(double.NegativeInfinity));
    }

    [Test]
    public void TryParse_RoundsAndClampsFiniteValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioGainValue.TryParse("3.26", CultureInfo.InvariantCulture, out var rounded), Is.True);
            Assert.That(rounded, Is.EqualTo(3.3d));
            Assert.That(AudioGainValue.TryParse("99", CultureInfo.InvariantCulture, out var upper), Is.True);
            Assert.That(upper, Is.EqualTo(20d));
            Assert.That(AudioGainValue.TryParse("-99", CultureInfo.InvariantCulture, out var lower), Is.True);
            Assert.That(lower, Is.EqualTo(-60d));
        });
    }

    [Test]
    public void Format_NegativeInfinityUsesSymbol()
    {
        Assert.That(AudioGainValue.Format(double.NegativeInfinity, CultureInfo.InvariantCulture), Is.EqualTo("-∞"));
    }
}
