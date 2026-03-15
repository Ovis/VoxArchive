using NAudio.Wave;

namespace VoxArchive.Wpf;

public sealed class StereoGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public StereoGainSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float LeftGain { get; set; } = 1f;
    public float RightGain { get; set; } = 1f;
    public bool MixToMono { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        var channels = Math.Max(1, WaveFormat.Channels);
        if (channels == 1)
        {
            for (var i = 0; i < read; i++)
            {
                buffer[offset + i] = Clip(buffer[offset + i] * LeftGain);
            }

            return read;
        }

        for (var i = 0; i < read; i += channels)
        {
            var leftIndex = offset + i;
            var rightIndex = leftIndex + 1;
            if (rightIndex >= offset + read)
            {
                break;
            }

            var left = buffer[leftIndex] * LeftGain;
            var right = buffer[rightIndex] * RightGain;

            if (MixToMono)
            {
                var mixed = Clip((left + right) * 0.5f);
                buffer[leftIndex] = mixed;
                buffer[rightIndex] = mixed;
            }
            else
            {
                buffer[leftIndex] = Clip(left);
                buffer[rightIndex] = Clip(right);
            }

            for (var ch = 2; ch < channels && (i + ch) < read; ch++)
            {
                buffer[offset + i + ch] = Clip(buffer[offset + i + ch]);
            }
        }

        return read;
    }

    private static float Clip(float sample)
    {
        if (sample > 1f)
        {
            return 1f;
        }

        if (sample < -1f)
        {
            return -1f;
        }

        return sample;
    }
}
