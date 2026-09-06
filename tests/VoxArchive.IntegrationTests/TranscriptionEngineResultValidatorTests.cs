using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

public sealed class TranscriptionEngineResultValidatorTests
{
    private readonly TranscriptionEngineResultValidator _validator = new();

    [Test]
    public void Validate_AcceptsOrderedAbsoluteTimeline()
    {
        var result = new TranscriptionEngineResult([
            new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a"),
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), "b")
        ]);

        Assert.That(
            () => _validator.Validate(result, TimeSpan.FromSeconds(3)),
            Throws.Nothing);
    }

    [Test]
    public void Validate_RejectsSegmentOutsideRecordingInsteadOfClamping()
    {
        var result = new TranscriptionEngineResult([
            new RecognizedTranscriptionSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), "text")
        ]);

        Assert.That(
            () => _validator.Validate(result, TimeSpan.FromSeconds(3)),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Validate_RejectsOutOfOrderTimeline()
    {
        var result = new TranscriptionEngineResult([
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5), "later"),
            new RecognizedTranscriptionSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), "earlier")
        ]);

        Assert.That(
            () => _validator.Validate(result, TimeSpan.FromSeconds(3)),
            Throws.TypeOf<InvalidDataException>());
    }
}
