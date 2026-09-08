using NAudio.Wave;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Audio PreparationがDuration逆算ではなく生成済みWAVの実データ長をsample座標の正本として保持することを確認する
/// </summary>
public sealed class TranscriptionAudioPreparationSampleCountTests
{
    [Test]
    public async Task PrepareAsync_PreservesExactGeneratedSampleCount()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"VoxArchive.Tests-{Guid.NewGuid():N}.wav");
        try
        {
            const int sampleRate = 16_000;
            const int sampleCount = 1_001;
            using (var writer = new WaveFileWriter(sourcePath, new WaveFormat(sampleRate, 16, 1)))
            {
                var bytes = new byte[sampleCount * sizeof(short)];
                writer.Write(bytes, 0, bytes.Length);
            }

            var service = new TranscriptionAudioPreparationService();
            await using var prepared = await service.PrepareAsync(
                sourcePath,
                speakerGainDb: 0,
                micGainDb: 0,
                new TranscriptionAudioRequirements(sampleRate, 1, TranscriptionSampleFormat.Pcm16));

            await using var stream = await prepared.OpenReadAsync();
            using var reader = new WaveFileReader(stream);
            var actualFromWave = reader.Length / reader.WaveFormat.BlockAlign;

            Assert.Multiple(() =>
            {
                Assert.That(prepared.SampleCount, Is.EqualTo(sampleCount));
                Assert.That(prepared.SampleCount, Is.EqualTo(actualFromWave));
            });
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
    }
}
