using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine固有ChunkerがVAD結果のsample範囲とlineageを維持してRecognitionChunkへ変換することを確認する
/// </summary>
public sealed class RecognitionChunkerTests
{
    [Test]
    public async Task Whisper_CreateChunksAsync_MapsSpeechRegionsOneToOne()
    {
        var regions = CreateRegions();
        var sut = new WhisperRecognitionChunker();

        var chunks = await sut.CreateChunksAsync(new TestPreparedAudio(), regions);

        AssertChunks(chunks, regions);
    }

    [Test]
    public async Task Whisper_CreateChunksWithDiagnosticsAsync_RecordsSpeechRegionReason()
    {
        var regions = CreateRegions();
        var sut = new WhisperRecognitionChunker();

        var result = await sut.CreateChunksWithDiagnosticsAsync(new TestPreparedAudio(), regions);

        AssertChunks(result.Chunks, regions);
        Assert.That(result.Traces, Has.Count.EqualTo(result.Chunks.Count));
        for (var i = 0; i < result.Traces.Count; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.Traces[i].RecognitionChunkId, Is.EqualTo(result.Chunks[i].RecognitionChunkId));
                Assert.That(result.Traces[i].SpeechRegionId, Is.EqualTo(result.Chunks[i].SpeechRegionId));
                Assert.That(result.Traces[i].StartSample, Is.EqualTo(result.Chunks[i].StartSample));
                Assert.That(result.Traces[i].EndSample, Is.EqualTo(result.Chunks[i].EndSample));
                Assert.That(result.Traces[i].SplitReason, Is.EqualTo("speech-region"));
            });
        }
    }

    [Test]
    public async Task ReazonSpeech_CreateChunksAsync_CurrentPhaseMapsSpeechRegionsOneToOne()
    {
        var regions = CreateRegions();
        var sut = new ReazonSpeechRecognitionChunker();

        var chunks = await sut.CreateChunksAsync(new TestPreparedAudio(), regions);

        AssertChunks(chunks, regions);
    }

    [Test]
    public async Task ReazonSpeech_CreateChunksWithDiagnosticsAsync_ShortRegionsUseSpeechRegionReason()
    {
        var regions = CreateRegions();
        var sut = new ReazonSpeechRecognitionChunker();

        var result = await sut.CreateChunksWithDiagnosticsAsync(new TestPreparedAudio(), regions);

        AssertChunks(result.Chunks, regions);
        Assert.That(result.Traces.Select(x => x.SplitReason), Is.All.EqualTo("speech-region"));
    }

    [Test]
    public void CreateChunksAsync_WhenCancelled_StopsBeforeChunkGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new WhisperRecognitionChunker().CreateChunksAsync(
                new TestPreparedAudio(),
                CreateRegions(),
                cancellation.Token));
    }

    private static IReadOnlyList<SpeechRegion> CreateRegions()
        =>
        [
            new SpeechRegion(3, 1_600, 16_000, [new AudioSampleRange(2_000, 15_500)], [7]),
            new SpeechRegion(8, 20_000, 32_000, [new AudioSampleRange(20_500, 31_500)], [9])
        ];

    private static void AssertChunks(
        IReadOnlyList<RecognitionChunk> chunks,
        IReadOnlyList<SpeechRegion> regions)
    {
        Assert.That(chunks, Has.Count.EqualTo(regions.Count));
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(chunks[i].RecognitionChunkId, Is.EqualTo(i));
                Assert.That(chunks[i].SpeechRegionId, Is.EqualTo(regions[i].SpeechRegionId));
                Assert.That(chunks[i].StartSample, Is.EqualTo(regions[i].StartSample));
                Assert.That(chunks[i].EndSample, Is.EqualTo(regions[i].EndSample));
            });
        }
    }

    private sealed class TestPreparedAudio : IPreparedTranscriptionAudio
    {
        public TranscriptionAudioRequirements Format { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);
        public TimeSpan Duration => TimeSpan.FromSeconds(10);
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
