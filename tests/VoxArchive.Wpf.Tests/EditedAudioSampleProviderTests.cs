using NAudio.Wave;
using NUnit.Framework;
using VoxArchive.Domain;

namespace VoxArchive.Wpf.Tests;

[TestFixture]
public sealed class EditedAudioSampleProviderTests
{
    [TestCase(1, AudioRenderChannelMode.MonoMixdown, false)]
    [TestCase(1, AudioRenderChannelMode.Stereo, false)]
    [TestCase(2, AudioRenderChannelMode.MonoMixdown, false)]
    [TestCase(2, AudioRenderChannelMode.Stereo, false)]
    [TestCase(2, AudioRenderChannelMode.MonoMixdown, true)]
    [TestCase(2, AudioRenderChannelMode.Stereo, true)]
    public async Task PlaybackMatchesExportIncludingCrossfadesAndSeeks(int channels, AudioRenderChannelMode mode, bool muteRight)
    {
        const int sampleRate = 1000;
        var directory = Path.Combine(Path.GetTempPath(), $"voxarchive-preview-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.wav");
            var rendered = Path.Combine(directory, "rendered.wav");
            using (var writer = new WaveFileWriter(source, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)))
            {
                var samples = new float[4000 * channels];
                for (var frame = 0; frame < 4000; frame++)
                {
                    for (var channel = 0; channel < channels; channel++)
                        samples[frame * channels + channel] = (float)(Math.Sin(frame * 0.037 + channel) * 0.3);
                }
                writer.WriteSamples(samples, 0, samples.Length);
            }

            var channelStates = channels == 1
                ? new[] { new AudioChannelEditState(3) }
                : new[] { new AudioChannelEditState(3), new AudioChannelEditState(-4, muteRight) };
            var state = new AudioEditState(TimeSpan.FromSeconds(4), channels,
                [new AudioCutRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)),
                 new AudioCutRange(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3))],
                channelStates);
            await AudioFileRenderService.RenderWaveAsync(source, rendered, state, mode, autoAttenuate: false);

            using var sourceReader = new AudioFileReader(source);
            using var expectedReader = new AudioFileReader(rendered);
            var preview = new EditedAudioSampleProvider(sourceReader, state, mode);
            var expected = new float[257 * expectedReader.WaveFormat.Channels];
            var actual = new float[expected.Length];
            long compared = 0;
            while (true)
            {
                var expectedCount = ((ISampleProvider)expectedReader).Read(expected.AsSpan());
                var actualCount = preview.Read(actual);
                Assert.That(actualCount, Is.EqualTo(expectedCount));
                if (expectedCount == 0) break;
                AssertSamplesMatch(expected, actual, expectedCount, $"frame {compared}");
                compared += expectedCount;
            }

            Assert.That(compared, Is.GreaterThan(0));
            foreach (var seconds in new[] { 0d, 0.994d, 1.003d, 1.499d, 1.503d, 2d, 2.99d })
            {
                var position = TimeSpan.FromSeconds(seconds);
                preview.Seek(position);
                expectedReader.Position = AudioRenderPlan.TimeToFrameIndex(position, sampleRate)
                    * expectedReader.WaveFormat.BlockAlign;
                var expectedCount = ((ISampleProvider)expectedReader).Read(expected.AsSpan());
                var actualCount = preview.Read(actual);
                Assert.That(actualCount, Is.EqualTo(expectedCount), $"seek at {seconds}s");
                AssertSamplesMatch(expected, actual, expectedCount, $"seek at {seconds}s");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertSamplesMatch(float[] expected, float[] actual, int count, string position)
    {
        for (var index = 0; index < count; index++)
            Assert.That(actual[index], Is.EqualTo(expected[index]).Within(0.000002f), $"{position}, sample {index}");
    }
}
