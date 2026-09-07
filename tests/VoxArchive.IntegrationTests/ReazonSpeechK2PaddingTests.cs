using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2入力の前後0.9秒無音とtimestamp補正を確認する
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
    public void Normalize_SubtractsPrePaddingAndConvertsToChunkRelativeSamples()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.Normalize(
            rawStartSeconds: 1.40d,
            rawEndSeconds: 2.90d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(normalized!.Value.StartSample, Is.EqualTo(8_000));
            Assert.That(normalized.Value.EndSample, Is.EqualTo(32_000));
        });
    }

    [Test]
    public void Normalize_ClampsTimestampThatStartsInsidePrePaddingToChunkStart()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.Normalize(
            rawStartSeconds: 0.20d,
            rawEndSeconds: 1.40d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(normalized!.Value.StartSample, Is.Zero);
            Assert.That(normalized.Value.EndSample, Is.EqualTo(8_000));
        });
    }

    [Test]
    public void Normalize_ClampsTimestampThatEndsInsidePostPaddingToChunkEnd()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.Normalize(
            rawStartSeconds: 10.40d,
            rawEndSeconds: 11.40d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(normalized!.Value.StartSample, Is.EqualTo(152_000));
            Assert.That(normalized.Value.EndSample, Is.EqualTo(160_000));
        });
    }

    [Test]
    public void Normalize_WhenCorrectedRangeHasNoLength_ReturnsNull()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.Normalize(
            rawStartSeconds: 0.10d,
            rawEndSeconds: 0.80d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Null);
    }

    [Test]
    public void Normalize_UsesFloorForStartAndCeilingForEnd()
    {
        var normalized = ReazonSpeechK2TimestampNormalizer.Normalize(
            rawStartSeconds: 0.90001d,
            rawEndSeconds: 0.90001d,
            chunkLengthSamples: 160_000);

        Assert.That(normalized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(normalized!.Value.StartSample, Is.Zero);
            Assert.That(normalized.Value.EndSample, Is.EqualTo(1));
        });
    }
}
