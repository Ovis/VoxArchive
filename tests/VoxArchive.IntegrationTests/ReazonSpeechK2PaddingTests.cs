using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2入力の前後0.9秒無音とsubword point timestamp補正を確認する
/// </summary>
public sealed class ReazonSpeechK2PaddingTests
{
    [Test]
    public void Apply_AddsZeroPaddingBeforeAndAfterWithoutChangingPayload()
    {
        var source = new[] { 0.25f, -0.50f, 0.75f };

        var padded = ReazonSpeechK2InputPadding.Apply(source);

        Assert.Multiple(() =>
        {
            Assert.That(padded, Has.Length.EqualTo(source.Length + (ReazonSpeechK2InputPadding.PaddingSamples * 2)));
            Assert.That(padded.Take(ReazonSpeechK2InputPadding.PaddingSamples), Is.All.EqualTo(0f));
            Assert.That(padded.Skip(ReazonSpeechK2InputPadding.PaddingSamples).Take(source.Length), Is.EqualTo(source));
            Assert.That(padded.Skip(ReazonSpeechK2InputPadding.PaddingSamples + source.Length), Is.All.EqualTo(0f));
        });
    }

    [Test]
    public void NormalizePoint_SubtractsPrePaddingAndConvertsFloatTimestampToChunkRelativeSample()
    {
        // sherpa-onnxの実APIと同じfloat値を経由させ、公称1.4秒が量子化されても1sample前倒しされないことを確認する。
        var normalized = ReazonSpeechK2TimestampNormalizer.NormalizePoint(
            rawSeconds: 1.40f,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.EqualTo(8_000));
    }

    [Test]
    public void NormalizePoint_ClampsTimestampInsidePrePaddingToChunkStart()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.NormalizePoint(
            rawSeconds: 0.20d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Zero);
    }

    [Test]
    public void NormalizePoint_ClampsTimestampInsidePostPaddingToChunkEnd()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.NormalizePoint(
            rawSeconds: 11.40d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.EqualTo(160_000));
    }

    [Test]
    public void NormalizePoint_RoundsPointTimestampToNearestSample()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.NormalizePoint(
            rawSeconds: 0.90004d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.EqualTo(1));
    }

    [Test]
    public void NormalizePoint_WhenTimestampIsNotFinite_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReazonSpeechK2TimestampNormalizer.NormalizePoint(
                double.NaN,
                chunkLengthSamples: 160_000));
    }
}
