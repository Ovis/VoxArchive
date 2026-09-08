using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Silero優先VADのfallback条件とsession内連続失敗制御を確認する
/// </summary>
public sealed class SileroPreferredSpeechRegionDetectorTests
{
    [Test]
    public async Task DetectAsync_WhenSileroReturnsNoRegions_DoesNotFallback()
    {
        var silero = new StubDetector((_, _, _) => Task.FromResult<IReadOnlyList<SpeechRegion>>([]));
        var fallback = new StubDetector((_, _, _) => Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(9)]));
        var warnings = new RecordingWarningSink();
        var sut = CreateSut(silero, fallback, warnings);

        var result = await sut.DetectAsync(new TestPreparedAudio(), EmptySettings());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Empty);
            Assert.That(silero.CallCount, Is.EqualTo(1));
            Assert.That(fallback.CallCount, Is.Zero);
            Assert.That(warnings.Codes, Is.Empty);
        });
    }

    [Test]
    public async Task DetectAsync_WhenSileroIsUnavailable_FallsBackWithoutSessionSuppression()
    {
        var silero = new StubDetector((_, _, _) =>
            throw new SileroVadUnavailableException("model unavailable"));
        var fallback = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(1)]));
        var warnings = new RecordingWarningSink();
        var sut = CreateSut(silero, fallback, warnings);
        var audio = new TestPreparedAudio();

        for (var i = 0; i < 4; i++)
        {
            var result = await sut.DetectAsync(audio, EmptySettings());
            Assert.That(result.Single().SpeechRegionId, Is.EqualTo(1));
        }

        Assert.Multiple(() =>
        {
            // モデル利用不可は推論失敗ではないため、3回を超えても次ジョブでSileroを再試行する。
            Assert.That(silero.CallCount, Is.EqualTo(4));
            Assert.That(fallback.CallCount, Is.EqualTo(4));
            Assert.That(warnings.Codes, Is.All.EqualTo("silero-unavailable"));
        });
    }

    [Test]
    public async Task DetectAsync_AfterThreeConsecutiveInferenceFailures_SuppressesSileroForSession()
    {
        var silero = new StubDetector((_, _, _) => throw new InvalidOperationException("inference failed"));
        var fallback = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(2)]));
        var warnings = new RecordingWarningSink();
        var sut = CreateSut(silero, fallback, warnings);
        var audio = new TestPreparedAudio();

        for (var i = 0; i < 4; i++)
        {
            await sut.DetectAsync(audio, EmptySettings());
        }

        Assert.Multiple(() =>
        {
            Assert.That(silero.CallCount, Is.EqualTo(3));
            Assert.That(fallback.CallCount, Is.EqualTo(4));
            Assert.That(warnings.Codes, Is.EqualTo(new[]
            {
                "silero-inference-failed",
                "silero-inference-failed",
                "silero-inference-failed-session-suppressed",
                "silero-session-suppressed"
            }));
        });
    }

    [Test]
    public async Task DetectAsync_WhenWarningSinkFails_StillUsesFallback()
    {
        var silero = new StubDetector((_, _, _) =>
            throw new SileroVadUnavailableException("model unavailable"));
        var fallback = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(7)]));
        var sut = CreateSut(silero, fallback, new ThrowingWarningSink());

        var result = await sut.DetectAsync(new TestPreparedAudio(), EmptySettings());

        Assert.Multiple(() =>
        {
            Assert.That(result.Single().SpeechRegionId, Is.EqualTo(7));
            Assert.That(fallback.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DetectAsync_WhenSileroSucceeds_ResetsConsecutiveInferenceFailures()
    {
        var outcomes = new Queue<Func<Task<IReadOnlyList<SpeechRegion>>>>(
        [
            () => throw new InvalidOperationException("failure 1"),
            () => throw new InvalidOperationException("failure 2"),
            () => Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(3)]),
            () => throw new InvalidOperationException("failure after reset 1"),
            () => throw new InvalidOperationException("failure after reset 2"),
            () => Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(4)]),
        ]);
        var silero = new StubDetector((_, _, _) => outcomes.Dequeue()());
        var fallback = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(8)]));
        var sut = CreateSut(silero, fallback);
        var audio = new TestPreparedAudio();

        for (var i = 0; i < 6; i++)
        {
            await sut.DetectAsync(audio, EmptySettings());
        }

        Assert.Multiple(() =>
        {
            Assert.That(silero.CallCount, Is.EqualTo(6));
            Assert.That(fallback.CallCount, Is.EqualTo(4));
        });
    }

    [Test]
    public void DetectAsync_WhenCancelled_DoesNotFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var silero = new StubDetector((_, _, token) => Task.FromCanceled<IReadOnlyList<SpeechRegion>>(token));
        var fallback = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(5)]));
        var sut = CreateSut(silero, fallback);

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await sut.DetectAsync(new TestPreparedAudio(), EmptySettings(), cancellation.Token));
        Assert.That(fallback.CallCount, Is.Zero);
    }

    [Test]
    public void DetectAsync_WhenFallbackAlsoFails_PropagatesFallbackFailure()
    {
        var silero = new StubDetector((_, _, _) => throw new InvalidOperationException("silero failure"));
        var fallback = new StubDetector((_, _, _) => throw new IOException("fallback failure"));
        var sut = CreateSut(silero, fallback);

        var exception = Assert.ThrowsAsync<IOException>(async () =>
            await sut.DetectAsync(new TestPreparedAudio(), EmptySettings()));

        Assert.That(exception!.Message, Is.EqualTo("fallback failure"));
    }

    private static SileroPreferredSpeechRegionDetector CreateSut(
        ISpeechRegionDetector silero,
        ISpeechRegionDetector fallback,
        ITranscriptionWarningSink? warningSink = null)
        => new(silero, fallback, NullLogger<SileroPreferredSpeechRegionDetector>.Instance, warningSink);

    private static SpeechRegionDetectorSettingsSnapshot EmptySettings()
        => new(1, default);

    private static SpeechRegion CreateRegion(int id)
        => new(id, 0, 1_600, [new AudioSampleRange(0, 1_600)], [id]);

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

    private sealed class RecordingWarningSink : ITranscriptionWarningSink
    {
        public List<string> Codes { get; } = [];

        public void Report(TranscriptionWarning warning) => Codes.Add(warning.Code);
    }

    private sealed class ThrowingWarningSink : ITranscriptionWarningSink
    {
        public void Report(TranscriptionWarning warning)
            => throw new InvalidOperationException("notification failure");
    }

    private sealed class TestPreparedAudio : IPreparedTranscriptionAudio
    {
        public TranscriptionAudioRequirements Format { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
