using System.Text;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2 Chunkerが通常無音、forced split、短い末尾再配分を使って25秒以下の連続chunkを生成することを確認する
/// </summary>
public sealed class ReazonSpeechRecognitionChunkerTests
{
    private const int SampleRate = 16_000;

    [Test]
    public async Task CreateChunksAsync_LongRegion_PrefersNormalSilenceNearTwentyFiveSeconds()
    {
        var samples = CreateSignal(40d, 0.80f);
        Fill(samples, 0d, 9d, 0.10f);
        Fill(samples, 24.4d, 24.8d, 0f);
        var audio = new MemoryPreparedAudio(samples);
        var region = CreateRegion(samples.Length);

        var chunks = await new ReazonSpeechRecognitionChunker().CreateChunksAsync(audio, [region]);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(chunks[0].EndSample, Is.InRange(Samples(24.55d), Samples(24.65d)));
            Assert.That(chunks[1].StartSample, Is.EqualTo(chunks[0].EndSample));
            Assert.That(chunks[0].Length, Is.LessThanOrEqualTo(Samples(25d)));
            Assert.That(chunks[1].Length, Is.LessThanOrEqualTo(Samples(25d)));
        });
    }

    [Test]
    public async Task CreateChunksAsync_WithoutNormalSilence_UsesMinimumRmsForcedSplit()
    {
        var samples = CreateSignal(40d, 0.80f);
        Fill(samples, 0d, 9d, 0.10f);
        Fill(samples, 24.38d, 24.45d, 0.40f);
        var audio = new MemoryPreparedAudio(samples);
        var region = CreateRegion(samples.Length);

        var chunks = await new ReazonSpeechRecognitionChunker().CreateChunksAsync(audio, [region]);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(chunks[0].EndSample, Is.InRange(Samples(24.35d), Samples(24.48d)));
            Assert.That(chunks[1].StartSample, Is.EqualTo(chunks[0].EndSample));
            Assert.That(chunks.All(x => x.Length <= Samples(25d)), Is.True);
        });
    }

    [Test]
    public async Task CreateChunksAsync_WhenTwentyFiveSecondSplitWouldLeaveShortTail_RedistributesAroundEqualHalves()
    {
        var samples = CreateSignal(26d, 0.80f);
        Fill(samples, 0d, 6d, 0.10f);
        Fill(samples, 13.35d, 13.48d, 0.30f);
        var audio = new MemoryPreparedAudio(samples);
        var region = CreateRegion(samples.Length);

        var chunks = await new ReazonSpeechRecognitionChunker().CreateChunksAsync(audio, [region]);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(chunks[0].EndSample, Is.InRange(Samples(12.8d), Samples(13.8d)));
            Assert.That(chunks[1].StartSample, Is.EqualTo(chunks[0].EndSample));
            Assert.That(chunks.All(x => x.Length >= Samples(3d)), Is.True);
            Assert.That(chunks.All(x => x.Length <= Samples(25d)), Is.True);
        });
    }

    [Test]
    public async Task CreateChunksAsync_VeryLongRegion_ProducesSequentialIdsAndContiguousChunksNoLongerThanTwentyFiveSeconds()
    {
        var samples = CreateSignal(60d, 0.80f);
        Fill(samples, 0d, 13d, 0.10f);
        var audio = new MemoryPreparedAudio(samples);
        var region = CreateRegion(samples.Length, speechRegionId: 7);

        var chunks = await new ReazonSpeechRecognitionChunker().CreateChunksAsync(audio, [region]);

        Assert.That(chunks.Count, Is.GreaterThanOrEqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(chunks[0].StartSample, Is.EqualTo(0));
            Assert.That(chunks[^1].EndSample, Is.EqualTo(samples.Length));
            Assert.That(chunks.All(x => x.Length > 0 && x.Length <= Samples(25d)), Is.True);
            Assert.That(chunks.All(x => x.SpeechRegionId == 7), Is.True);
        });

        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.That(chunks[i].RecognitionChunkId, Is.EqualTo(i));
            if (i > 0)
            {
                Assert.That(chunks[i].StartSample, Is.EqualTo(chunks[i - 1].EndSample));
            }
        }
    }

    private static SpeechRegion CreateRegion(int sampleCount, int speechRegionId = 0)
        => new(
            speechRegionId,
            0,
            sampleCount,
            [new AudioSampleRange(0, sampleCount)],
            [0]);

    private static float[] CreateSignal(double seconds, float amplitude)
        => Enumerable.Repeat(amplitude, checked((int)Samples(seconds))).ToArray();

    private static void Fill(float[] samples, double startSeconds, double endSeconds, float value)
    {
        var start = checked((int)Samples(startSeconds));
        var end = checked((int)Samples(endSeconds));
        Array.Fill(samples, value, start, end - start);
    }

    private static long Samples(double seconds) => (long)Math.Round(seconds * SampleRate);

    private sealed class MemoryPreparedAudio(float[] samples) : IPreparedTranscriptionAudio
    {
        private readonly byte[] _wave = CreatePcm16Wave(samples);

        public TranscriptionAudioRequirements Format { get; } =
            new(SampleRate, 1, TranscriptionSampleFormat.Pcm16);

        public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / (double)SampleRate);

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_wave, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] CreatePcm16Wave(IReadOnlyList<float> samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataLength = checked(samples.Count * sizeof(short));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (var sample in samples)
            {
                writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
            }
        }
        return stream.ToArray();
    }
}
