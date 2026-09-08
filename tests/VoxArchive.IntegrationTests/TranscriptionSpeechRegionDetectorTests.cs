using NAudio.Wave;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

public sealed class TranscriptionSpeechRegionDetectorTests
{
    private const int SampleRate = 16_000;
    private static readonly SpeechRegionDetectorSettingsSnapshot Settings = new(1, default);

    [Test]
    public async Task DetectAsync_ClampsRegionEndToPreparedAudioDuration()
    {
        var waveBytes = BuildWaveWithSpeechAtEnd();
        await using var audio = new TestPreparedAudio(waveBytes, TimeSpan.FromMilliseconds(950));
        var sut = new TranscriptionSpeechRegionDetector();

        var regions = await sut.DetectAsync(audio, Settings);

        var maximumSample = audio.SampleCount;
        Assert.That(regions, Is.Not.Empty);
        Assert.That(regions[^1].EndSample, Is.EqualTo(maximumSample));
        Assert.That(regions.All(x => x.EndSample <= maximumSample), Is.True);
    }

    [Test]
    public async Task DetectAsync_PreservesCoreRangeAndSourceLineage()
    {
        var waveBytes = BuildWaveWithSpeechAtEnd();
        await using var audio = new TestPreparedAudio(waveBytes, TimeSpan.FromSeconds(1));
        var sut = new TranscriptionSpeechRegionDetector();

        var regions = await sut.DetectAsync(audio, Settings);

        Assert.That(regions, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(regions[0].SpeechRegionId, Is.EqualTo(0));
            Assert.That(regions[0].CoreRanges, Is.Not.Empty);
            Assert.That(regions[0].SourceRawSpeechRegionIds, Is.EqualTo(new[] { 0 }));
            Assert.That(regions[0].CoreRanges.All(x => x.StartSample >= regions[0].StartSample), Is.True);
            Assert.That(regions[0].CoreRanges.All(x => x.EndSample <= regions[0].EndSample), Is.True);
        });
    }

    [Test]
    public async Task DetectWithDiagnosticsAsync_ReturnsRawRegionMatchingFinalLineage()
    {
        var waveBytes = BuildWaveWithSpeechAtEnd();
        await using var audio = new TestPreparedAudio(waveBytes, TimeSpan.FromSeconds(1));
        var sut = new TranscriptionSpeechRegionDetector();

        var result = await sut.DetectWithDiagnosticsAsync(audio, Settings);

        Assert.That(result.SpeechRegions, Has.Count.EqualTo(1));
        Assert.That(result.Trace.RawRegions, Has.Count.EqualTo(1));
        var region = result.SpeechRegions[0];
        var raw = result.Trace.RawRegions[0];
        var core = region.CoreRanges.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Trace.Detector, Is.EqualTo("VolumeBasedVad"));
            Assert.That(result.Trace.FallbackUsed, Is.False);
            Assert.That(result.Trace.FallbackReason, Is.Null);
            Assert.That(raw.RawSpeechRegionId, Is.EqualTo(region.SourceRawSpeechRegionIds.Single()));
            Assert.That(raw.StartSample, Is.EqualTo(core.StartSample));
            Assert.That(raw.EndSample, Is.EqualTo(core.EndSample));
        });
    }

    private static byte[] BuildWaveWithSpeechAtEnd()
    {
        const int totalSamples = SampleRate;
        const int speechStartSample = SampleRate / 2;
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            var buffer = new byte[totalSamples * 2];
            for (var i = speechStartSample; i < totalSamples; i++)
            {
                const short sample = 12_000;
                buffer[i * 2] = (byte)(sample & 0xFF);
                buffer[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
            }
            writer.Write(buffer, 0, buffer.Length);
        }
        return stream.ToArray();
    }

    private sealed class TestPreparedAudio(byte[] waveBytes, TimeSpan duration) : IPreparedTranscriptionAudio
    {
        public TranscriptionAudioRequirements Format { get; } = new(SampleRate, 1, TranscriptionSampleFormat.Pcm16);
        public TimeSpan Duration { get; } = duration;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(waveBytes, writable: false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
