using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2のsubword timestampがraw、chunk-relative、absolute sampleへ正しく対応付くことを確認する
/// </summary>
public sealed class ReazonSpeechK2TokenTimestampTraceTests
{
    [Test]
    public void Build_MapsRawTimestampThroughPaddingCorrectionToAbsoluteSample()
    {
        var chunk = new RecognitionChunk(4, 2, 160_000, 320_000);

        var trace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
            ["こ", "ん"],
            [1.40f, 2.90f],
            chunk);

        Assert.That(trace, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(trace[0].TokenIndex, Is.Zero);
            Assert.That(trace[0].Token, Is.EqualTo("こ"));
            Assert.That(trace[0].CorrectedChunkSample, Is.EqualTo(8_000));
            Assert.That(trace[0].AbsoluteSample, Is.EqualTo(168_000));

            Assert.That(trace[1].TokenIndex, Is.EqualTo(1));
            Assert.That(trace[1].Token, Is.EqualTo("ん"));
            Assert.That(trace[1].CorrectedChunkSample, Is.EqualTo(32_000));
            Assert.That(trace[1].AbsoluteSample, Is.EqualTo(192_000));
        });
    }

    [Test]
    public void Build_ClampsPreAndPostPaddingPointsToChunkBounds()
    {
        var chunk = new RecognitionChunk(0, 0, 50_000, 210_000);

        var trace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
            ["a", "b"],
            [0.20f, 11.40f],
            chunk);

        Assert.Multiple(() =>
        {
            Assert.That(trace[0].CorrectedChunkSample, Is.Zero);
            Assert.That(trace[0].AbsoluteSample, Is.EqualTo(50_000));
            Assert.That(trace[1].CorrectedChunkSample, Is.EqualTo(160_000));
            Assert.That(trace[1].AbsoluteSample, Is.EqualTo(210_000));
        });
    }

    [Test]
    public void Build_WhenTokenAndTimestampCountsDiffer_UsesOnlyCorrespondingPairs()
    {
        var chunk = new RecognitionChunk(0, 0, 0, 160_000);

        var trace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
            ["a", "b", "c"],
            [1.0f, 2.0f],
            chunk);

        Assert.That(trace, Has.Count.EqualTo(2));
        Assert.That(trace.Select(x => x.Token), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Build_WhenTimestampIsNotFinite_SkipsOnlyThatTraceEntry()
    {
        var chunk = new RecognitionChunk(0, 0, 0, 160_000);

        var trace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
            ["a", "b", "c"],
            [1.0f, float.NaN, 2.0f],
            chunk);

        Assert.That(trace, Has.Count.EqualTo(2));
        Assert.That(trace.Select(x => x.TokenIndex), Is.EqualTo(new[] { 0, 2 }));
    }

    [Test]
    public void Build_WhenTimestampDataIsMissing_ReturnsEmptyTrace()
    {
        var chunk = new RecognitionChunk(0, 0, 0, 160_000);

        var trace = ReazonSpeechK2TokenTimestampTraceBuilder.Build(
            ["a"],
            null,
            chunk);

        Assert.That(trace, Is.Empty);
    }
}
