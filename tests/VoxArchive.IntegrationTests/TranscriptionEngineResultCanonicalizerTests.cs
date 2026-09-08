using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine非依存canonical規則が文字列とsample timelineへ一貫して適用されることを確認する
/// </summary>
public sealed class TranscriptionEngineResultCanonicalizerTests
{
    private const int SampleRate = 16_000;

    [Test]
    public void Canonicalize_TrimsDropsWhitespaceAndStableSortsByStartSample()
    {
        var result = new TranscriptionEngineResult(
        [
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.7), " third ", 2),
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.2), " first ", 0),
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.3), " second ", 1),
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.5), " \t ", 3),
        ]);

        var canonical = new TranscriptionEngineResultCanonicalizer().Canonicalize(
            result,
            new TestPreparedAudio(SampleRate));

        Assert.That(canonical.Segments, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(canonical.Segments.Select(x => x.Text), Is.EqualTo(new[] { "first", "second", "third" }));
            Assert.That(canonical.Segments.Select(x => x.RecognitionChunkId), Is.EqualTo(new int?[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void Canonicalize_UsesFloorForStartCeilForEndAndClampsToPreparedAudio()
    {
        var startSeconds = 1_600.5d / SampleRate;
        var endSeconds = 3_200.5d / SampleRate;
        var result = new TranscriptionEngineResult(
        [
            new RecognizedTranscriptionSegment(
                TimeSpan.FromSeconds(startSeconds),
                TimeSpan.FromSeconds(endSeconds),
                "sample-rounded"),
            new RecognizedTranscriptionSegment(
                TimeSpan.FromSeconds(-0.5),
                TimeSpan.FromSeconds(2),
                "clamped"),
        ]);

        var canonical = new TranscriptionEngineResultCanonicalizer().Canonicalize(
            result,
            new TestPreparedAudio(SampleRate));

        var rounded = canonical.Segments.Single(x => x.Text == "sample-rounded");
        var clamped = canonical.Segments.Single(x => x.Text == "clamped");
        Assert.Multiple(() =>
        {
            Assert.That(ToSamples(rounded.Start), Is.EqualTo(1_600));
            Assert.That(ToSamples(rounded.End), Is.EqualTo(3_201));
            Assert.That(ToSamples(clamped.Start), Is.Zero);
            Assert.That(ToSamples(clamped.End), Is.EqualTo(SampleRate));
        });
    }

    [Test]
    public void Canonicalize_PreservesEngineMetadataAndDiagnostics()
    {
        var metadata = new Dictionary<string, object?> { ["backend"] = "test" };
        var diagnostics = new TranscriptionEngineDiagnosticTrace([], 12, 34);
        var result = new TranscriptionEngineResult(
            [new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(100), " text ")],
            metadata,
            diagnostics);

        var canonical = new TranscriptionEngineResultCanonicalizer().Canonicalize(
            result,
            new TestPreparedAudio(SampleRate));

        Assert.Multiple(() =>
        {
            Assert.That(canonical.Metadata, Is.SameAs(metadata));
            Assert.That(canonical.Diagnostics, Is.SameAs(diagnostics));
        });
    }

    [Test]
    public void Canonicalize_WhenEndIsBeforeStart_Throws()
    {
        var result = new TranscriptionEngineResult(
        [
            new RecognizedTranscriptionSegment(
                TimeSpan.FromSeconds(0.5),
                TimeSpan.FromSeconds(0.4),
                "invalid")
        ]);

        Assert.Throws<InvalidDataException>(() =>
            new TranscriptionEngineResultCanonicalizer().Canonicalize(
                result,
                new TestPreparedAudio(SampleRate)));
    }

    private static long ToSamples(TimeSpan value)
        => (long)Math.Round(value.TotalSeconds * SampleRate);

    private sealed class TestPreparedAudio(int sampleCount) : IPreparedTranscriptionAudio
    {
        public TranscriptionAudioRequirements Format { get; } =
            new(SampleRate, 1, TranscriptionSampleFormat.Pcm16);

        public long SampleCount { get; } = sampleCount;
        public TimeSpan Duration => TimeSpan.FromSeconds(sampleCount / (double)SampleRate);

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
