using System.Text;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech K2 chunking用RMS系列のwindow/hopとCoreRanges限定P20を確認する
/// </summary>
public sealed class ReazonSpeechRmsAnalyzerTests
{
    [Test]
    public async Task AnalyzeAsync_UsesThirtyMillisecondWindowAndTenMillisecondHop()
    {
        var samples = Enumerable.Repeat(0.25f, 1_600).ToArray();
        var audio = new MemoryPreparedAudio(samples);
        var region = new SpeechRegion(
            0,
            0,
            samples.Length,
            [new AudioSampleRange(0, samples.Length)],
            [0]);

        var analysis = await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Frames, Has.Count.EqualTo(8));
            Assert.That(analysis.Frames[0].StartSample, Is.EqualTo(0));
            Assert.That(analysis.Frames[0].EndSample, Is.EqualTo(480));
            Assert.That(analysis.Frames[1].StartSample, Is.EqualTo(160));
            Assert.That(analysis.Frames[1].EndSample, Is.EqualTo(640));
            Assert.That(analysis.Frames[^1].StartSample, Is.EqualTo(1_120));
            Assert.That(analysis.Frames[^1].EndSample, Is.EqualTo(1_600));
        });
    }

    [Test]
    public async Task AnalyzeAsync_ComputesP20OnlyFromFramesFullyInsideCoreRanges()
    {
        var samples = Enumerable.Repeat(0.90f, 3_200).ToArray();
        for (var i = 800; i < 2_400; i++)
        {
            samples[i] = 0.20f;
        }
        var audio = new MemoryPreparedAudio(samples);
        var region = new SpeechRegion(
            0,
            0,
            samples.Length,
            [new AudioSampleRange(800, 2_400)],
            [0]);

        var analysis = await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region);

        // PCM16量子化後も約0.2になる。padding側の0.9がP20へ混入していないことを同時に確認する。
        Assert.That(analysis.P20Rms, Is.EqualTo(0.20d).Within(0.001d));
    }

    [Test]
    public async Task AnalyzeAsync_AllowsZeroP20ForSilentCore()
    {
        var samples = Enumerable.Repeat(0.75f, 3_200).ToArray();
        Array.Fill(samples, 0f, 800, 1_600);
        var audio = new MemoryPreparedAudio(samples);
        var region = new SpeechRegion(
            0,
            0,
            samples.Length,
            [new AudioSampleRange(800, 2_400)],
            [0]);

        var analysis = await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region);

        Assert.That(analysis.P20Rms, Is.Zero);
    }

    [Test]
    public void AnalyzeAsync_WhenCancelled_StopsAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var samples = Enumerable.Repeat(0.25f, 1_600).ToArray();
        var audio = new MemoryPreparedAudio(samples);
        var region = new SpeechRegion(0, 0, samples.Length, [new AudioSampleRange(0, samples.Length)], [0]);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region, cancellation.Token));
    }

    private sealed class MemoryPreparedAudio(float[] samples) : IPreparedTranscriptionAudio
    {
        private readonly byte[] _wave = CreatePcm16Wave(samples);

        public TranscriptionAudioRequirements Format { get; } =
            new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / 16_000d);

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
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            var dataLength = checked(samples.Count * sizeof(short));
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16_000);
            writer.Write(32_000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (var sample in samples)
            {
                writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
            }
        }
        return stream.ToArray();
    }
}
