using NAudio.Wave;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

public sealed class TranscriptionSpeechRegionDetectorTests
{
    [Test]
    public async Task DetectAsync_ClampsRegionEndToPreparedAudioDuration()
    {
        var waveBytes = BuildWaveWithSpeechAtEnd();
        await using var audio = new TestPreparedAudio(
            waveBytes,
            TimeSpan.FromMilliseconds(950));
        var sut = new TranscriptionSpeechRegionDetector();

        var regions = await sut.DetectAsync(audio);

        Assert.That(regions, Is.Not.Empty);
        Assert.That(regions[^1].End, Is.EqualTo(audio.Duration));
        Assert.That(regions.All(x => x.End <= audio.Duration), Is.True);
    }

    private static byte[] BuildWaveWithSpeechAtEnd()
    {
        const int sampleRate = 16_000;
        const int totalSamples = sampleRate;
        const int speechStartSample = sampleRate / 2;
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, 1)))
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
        public TranscriptionAudioRequirements Format { get; }
            = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public TimeSpan Duration { get; } = duration;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(waveBytes, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
