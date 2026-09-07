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
    public void Whisper_CreateChunks_MapsSpeechRegionsOneToOne()
    {
        var regions = CreateRegions();
        var sut = new WhisperRecognitionChunker();

        var chunks = sut.CreateChunks(new TestPreparedAudio(), regions);

        AssertChunks(chunks, regions);
    }

    [Test]
    public void ReazonSpeech_CreateChunks_CurrentPhaseMapsSpeechRegionsOneToOne()
    {
        var regions = CreateRegions();
        var sut = new ReazonSpeechRecognitionChunker();

        var chunks = sut.CreateChunks(new TestPreparedAudio(), regions);

        AssertChunks(chunks, regions);
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
