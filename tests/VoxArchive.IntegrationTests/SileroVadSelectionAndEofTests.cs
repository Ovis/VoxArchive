using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// VAD方式の明示選択とSilero EOF補完の境界条件を確認する
/// </summary>
public sealed class SileroVadSelectionAndEofTests
{
    [Test]
    public async Task DetectWithDiagnosticsAsync_WhenVolumeBasedSelected_BypassesSileroAndIsNotFallback()
    {
        var silero = new StubDetector((_, _, _) => throw new InvalidOperationException("Silero must not be called."));
        var volumeBased = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(10)]));
        var warnings = new RecordingWarningSink();
        var sut = new SileroPreferredSpeechRegionDetector(
            silero,
            volumeBased,
            NullLogger<SileroPreferredSpeechRegionDetector>.Instance,
            warnings);

        var result = await sut.DetectWithDiagnosticsAsync(
            new TestPreparedAudio(),
            VolumeBasedSettings());

        Assert.Multiple(() =>
        {
            Assert.That(result.SpeechRegions.Single().SpeechRegionId, Is.EqualTo(10));
            Assert.That(silero.CallCount, Is.Zero);
            Assert.That(volumeBased.CallCount, Is.EqualTo(1));
            Assert.That(result.Trace.Detector, Is.EqualTo("VolumeBasedVad"));
            Assert.That(result.Trace.FallbackUsed, Is.False);
            Assert.That(result.Trace.FallbackReason, Is.Null);
            Assert.That(warnings.Codes, Is.Empty);
        });
    }

    [Test]
    public async Task DetectAsync_WhenModeIsMissing_DefaultsToSilero()
    {
        var silero = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(20)]));
        var volumeBased = new StubDetector((_, _, _) =>
            Task.FromResult<IReadOnlyList<SpeechRegion>>([CreateRegion(21)]));
        var sut = new SileroPreferredSpeechRegionDetector(
            silero,
            volumeBased,
            NullLogger<SileroPreferredSpeechRegionDetector>.Instance);

        var result = await sut.DetectAsync(new TestPreparedAudio(), new SpeechRegionDetectorSettingsSnapshot(1, default));

        Assert.Multiple(() =>
        {
            Assert.That(result.Single().SpeechRegionId, Is.EqualTo(20));
            Assert.That(silero.CallCount, Is.EqualTo(1));
            Assert.That(volumeBased.CallCount, Is.Zero);
        });
    }

    [TestCase(512, 0)]
    [TestCase(511, 1)]
    [TestCase(1, 511)]
    public void PadEofWindow_AddsOnlyMinimumRequiredZeroSamples(int realSampleCount, int expectedPadding)
    {
        var window = Enumerable.Repeat(0.75f, 512).ToArray();

        var padding = SileroVadDetector.PadEofWindow(window, realSampleCount);

        Assert.That(padding, Is.EqualTo(expectedPadding));
        Assert.That(window.Take(realSampleCount), Is.All.EqualTo(0.75f));
        Assert.That(window.Skip(realSampleCount), Is.All.EqualTo(0f));
    }

    [Test]
    public void PadEofWindow_WhenNoSamples_DoesNotCreateArtificialInput()
    {
        var window = Enumerable.Repeat(0.75f, 512).ToArray();

        var padding = SileroVadDetector.PadEofWindow(window, 0);

        Assert.Multiple(() =>
        {
            Assert.That(padding, Is.Zero);
            Assert.That(window, Is.All.EqualTo(0.75f));
        });
    }

    [Test]
    public void ClampRawRegions_RemovesArtificialSamplesBeyondPreparedAudio()
    {
        var ranges = new[]
        {
            new AudioSampleRange(100, 600),
            new AudioSampleRange(700, 900),
        };

        var result = SileroVadDetector.ClampRawRegions(ranges, sampleCount: 750);

        Assert.That(result, Is.EqualTo(new[]
        {
            new AudioSampleRange(100, 600),
            new AudioSampleRange(700, 750),
        }));
    }

    [Test]
    public void ClampRawRegions_WhenAudioIsEmpty_ReturnsNoRegions()
    {
        var result = SileroVadDetector.ClampRawRegions(
            [new AudioSampleRange(0, 512)],
            sampleCount: 0);

        Assert.That(result, Is.Empty);
    }

    private static SpeechRegionDetectorSettingsSnapshot VolumeBasedSettings()
        => new(1, JsonSerializer.SerializeToElement(new { Mode = 1 }));

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
