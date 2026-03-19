using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class LinearVariableRateResampler : IVariableRateResampler
{
    private float _prevSample;
    private bool _hasPrev;

    public void Reset()
    {
        _prevSample = 0;
        _hasPrev = false;
    }

    public int Resample(ReadOnlySpan<float> source, Span<float> destination, double ratio, out int consumedSamples)
    {
        consumedSamples = 0;
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (source.IsEmpty)
        {
            destination.Clear();
            return destination.Length;
        }

        var safeRatio = Math.Clamp(ratio, 0.95d, 1.05d);
        var position = 0d;
        var sourceCursor = 0;

        if (!_hasPrev)
        {
            _prevSample = source[0];
            _hasPrev = true;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var idx = sourceCursor + (int)position;
            var frac = (float)(position - Math.Floor(position));

            float s0;
            float s1;

            if (idx <= 0)
            {
                s0 = _prevSample;
                s1 = source[0];
            }
            else if (idx >= source.Length)
            {
                s0 = source[^1];
                s1 = source[^1];
            }
            else if (idx == source.Length - 1)
            {
                s0 = source[idx];
                s1 = source[idx];
            }
            else
            {
                s0 = source[idx];
                s1 = source[idx + 1];
            }

            destination[i] = s0 + ((s1 - s0) * frac);

            position += safeRatio;
            var advance = (int)position;
            if (advance > 0)
            {
                position -= advance;
                sourceCursor += advance;
            }
        }

        consumedSamples = Math.Min(sourceCursor, source.Length);
        _prevSample = source[Math.Max(consumedSamples - 1, 0)];
        return destination.Length;
    }
}
