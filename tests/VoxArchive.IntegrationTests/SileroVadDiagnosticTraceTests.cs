using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Silero優先VADがraw regionとfallback理由をJob単位の診断traceとして返すことを確認する
/// </summary>
public sealed class SileroVadDiagnosticTraceTests
{
    [Test]
    public async Task DetectWithDiagnosticsAsync_WhenSileroSucceeds_PreservesRawRegions()
    {
        var region = CreateRegion(4);
        var silero = new DiagnosticStubDetector((_, _, _) => Task.FromResult(
            new SpeechRegionDetectionDiagnosticResult(
                [region],
                new SpeechRegionDetectionDiagnosticTrace(
                    "SileroVad",
                    [new SpeechRegionDetectionRawRegion(7, 120, 1_480)],
                    false,
                    null))));
        var fallback = new StubDetector((_, _, _) => Task.FromResult<IReadOnlyList<SpeechRegion>>([]));
        var sut = CreateSut(silero, fallback);

        var result = await sut.DetectWithDiagnosticsAsync(new TestPreparedAudio(), EmptySettings());

        Assert.Multiple(() =>
        {
            Assert.That(result.SpeechRegions.Single().SpeechRegionId, Is.EqualTo(4));
            Assert.That(result.Trace.Detector, Is.EqualTo("SileroVad"));
            Assert.That(result.Trace.FallbackUsed, Is.False);
            Assert.That(result.Trace.FallbackReason, Is.Null);
            Assert.That(result.Trace.RawRegions, Has.Count.EqualTo(1));
            Assert.That(result.Trace.RawRegions[0], Is.EqualTo(new SpeechRegionDetectionRawRegion(7, 120, 1_480)));
            Assert.That(fallback.CallCount, Is.Zero);
        });
    }

    [Test]
    public async Task DetectWithDiagnosticsAsync_WhenSileroUnavailable_RecordsFallbackReason()
    {
        var silero = new StubDetector((_, _, _) => throw new SileroVadUnavailableException("missing"));
        var fallback = new StubDetector((_, _, _) => Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(1)]));
        var sut = CreateSut(silero, fallback);

        var result = await sut.DetectWithDiagnosticsAsync(new TestPreparedAudio(), EmptySettings());

        Assert.Multiple(() =>
        {
            Assert.That(result.Trace.Detector, Is.EqualTo("VolumeBasedVad"));
            Assert.That(result.Trace.FallbackUsed, Is.True);
            Assert.That(result.Trace.FallbackReason, Is.EqualTo("silero-unavailable"));
            Assert.That(result.Trace.RawRegions, Is.Empty);
        });
    }

    [Test]
    public async Task DetectWithDiagnosticsAsync_AfterInferenceFailures_RecordsFailureAndSessionSuppressionReasons()
    {
        var silero = new StubDetector((_, _, _) => throw new InvalidOperationException("inference"));
        var fallback = new StubDetector((_, _, _) => Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(2)]));
        var sut = CreateSut(silero, fallback);
        var audio = new TestPreparedAudio();

        SpeechRegionDetectionDiagnosticResult? third = null;
        for (var i = 0; i < 3; i++)
        {
            third = await sut.DetectWithDiagnosticsAsync(audio, EmptySettings());
        }
        var fourth = await sut.DetectWithDiagnosticsAsync(audio, EmptySettings());

        Assert.Multiple(() =>
        {
            Assert.That(third!.Trace.FallbackReason, Is.EqualTo("silero-inference-failed"));
            Assert.That(fourth.Trace.FallbackReason, Is.EqualTo("session-suppressed"));
            Assert.That(silero.CallCount, Is.EqualTo(3));
            Assert.That(fallback.CallCount, Is.EqualTo(4));
        });
    }

    private static SileroPreferredSpeechRegionDetector CreateSut(
        ISpeechRegionDetector silero,
        ISpeechRegionDetector fallback)
        => new(silero, fallback, NullLogger<SileroPreferredSpeechRegionDetector>.Instance);

    private static SpeechRegionDetectorSettingsSnapshot EmptySettings() => new(1, default);

    private static SpeechRegion CreateRegion(int id)
        => new(id, 0, 1_600, [new AudioSampleRange(100, 1_500)], [id]);

    private sealed class StubDetector(
        Func<IPreparedTranscriptionAudio, SpeechRegionDetectorSettingsSnapshot, CancellationToken, Task<IReadOnlyList<SpeechRegion>>> handler)
        : ISpeechRegionDetector
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return handler(audio, settings, cancellationToken);
        }
    }

    private sealed class DiagnosticStubDetector(
        Func<IPreparedTranscriptionAudio, SpeechRegionDetectorSettingsSnapshot, CancellationToken, Task<SpeechRegionDetectionDiagnosticResult>> handler)
        : IDiagnosticSpeechRegionDetector
    {
        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken = default)
            => DetectCoreAsync(audio, settings, cancellationToken);

        public Task<SpeechRegionDetectionDiagnosticResult> DetectWithDiagnosticsAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken = default)
            => handler(audio, settings, cancellationToken);

        private async Task<IReadOnlyList<SpeechRegion>> DetectCoreAsync(
            IPreparedTranscriptionAudio audio,
            SpeechRegionDetectorSettingsSnapshot settings,
            CancellationToken cancellationToken)
            => (await handler(audio, settings, cancellationToken)).SpeechRegions;
    }

    private sealed class TestPreparedAudio : IPreparedTranscriptionAudio
    {
        public TranscriptionAudioRequirements Format { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public long SampleCount => 16_000;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
