using NAudio.Wave;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 話者ラベル診断が実際の判定に使用したCH1/CH2エネルギーをそのまま保持することを確認する
/// </summary>
public sealed class TranscriptionSpeakerLabelServiceTests
{
    [Test]
    public void ApplyWithDiagnostics_LeftDominant_ReturnsSpeakerWithSameEnergyEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-speaker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "stereo.wav");
        try
        {
            WriteStereoWave(path, leftAmplitude: 0.8f, rightAmplitude: 0.1f);
            var segment = new RecognizedTranscriptionSegment(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500),
                "test",
                RecognitionChunkId: 4);
            var sut = new TranscriptionSpeakerLabelService();

            var result = sut.ApplyWithDiagnostics(path, [segment]);

            Assert.That(result.Segments, Has.Count.EqualTo(1));
            Assert.That(result.Traces, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(result.Segments[0].SpeakerLabel, Is.EqualTo("Speaker"));
                Assert.That(result.Traces[0].RecognitionChunkId, Is.EqualTo(4));
                Assert.That(result.Traces[0].SpeakerLabel, Is.EqualTo("Speaker"));
                Assert.That(result.Traces[0].SpeakerChannelEnergy, Is.GreaterThan(0));
                Assert.That(result.Traces[0].MicrophoneChannelEnergy, Is.GreaterThan(0));
                Assert.That(
                    result.Traces[0].SpeakerChannelEnergy,
                    Is.GreaterThan(result.Traces[0].MicrophoneChannelEnergy));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ApplyWithDiagnostics_MonoAudio_PreservesExistingNullLabelAndDoesNotInventEnergy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"voxarchive-speaker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mono.wav");
        try
        {
            WriteMonoWave(path, amplitude: 0.5f);
            var segment = new RecognizedTranscriptionSegment(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500),
                "test",
                RecognitionChunkId: 2);
            var sut = new TranscriptionSpeakerLabelService();

            var result = sut.ApplyWithDiagnostics(path, [segment]);

            Assert.Multiple(() =>
            {
                Assert.That(result.Segments.Single().SpeakerLabel, Is.Null);
                Assert.That(result.Traces.Single().RecognitionChunkId, Is.EqualTo(2));
                Assert.That(result.Traces.Single().SpeakerLabel, Is.Null);
                Assert.That(result.Traces.Single().SpeakerChannelEnergy, Is.Null);
                Assert.That(result.Traces.Single().MicrophoneChannelEnergy, Is.Null);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteStereoWave(string path, float leftAmplitude, float rightAmplitude)
    {
        const int sampleRate = 16_000;
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        for (var i = 0; i < sampleRate / 2; i++)
        {
            writer.WriteSample(leftAmplitude);
            writer.WriteSample(rightAmplitude);
        }
    }

    private static void WriteMonoWave(string path, float amplitude)
    {
        const int sampleRate = 16_000;
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        for (var i = 0; i < sampleRate / 2; i++)
        {
            writer.WriteSample(amplitude);
        }
    }
}
