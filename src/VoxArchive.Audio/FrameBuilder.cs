using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class FrameBuilder : IFrameBuilder
{
    private readonly IRingBuffer _speakerBuffer;
    private readonly IRingBuffer _micBuffer;
    private readonly IVariableRateResampler _resampler;

    private float[] _speakerFrame = Array.Empty<float>();
    private float[] _micRawFrame = Array.Empty<float>();
    private float[] _micFrame = Array.Empty<float>();
    private byte[] _pcm16Interleaved = Array.Empty<byte>();

    public FrameBuilder(IRingBuffer speakerBuffer, IRingBuffer micBuffer, IVariableRateResampler resampler)
    {
        _speakerBuffer = speakerBuffer;
        _micBuffer = micBuffer;
        _resampler = resampler;
    }

    public FrameBuildResult BuildFrame(int frameSamples, double micRatio)
    {
        if (frameSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSamples));
        }

        EnsureCapacity(frameSamples);

        var speakerRead = _speakerBuffer.Read(_speakerFrame);
        if (speakerRead < frameSamples)
        {
            _speakerFrame.AsSpan(speakerRead).Clear();
        }

        var micRead = _micBuffer.Read(_micRawFrame);
        if (micRead < frameSamples)
        {
            _micRawFrame.AsSpan(micRead).Clear();
        }

        _resampler.Resample(_micRawFrame, _micFrame, micRatio, out _);
        InterleaveToPcm16(_speakerFrame, _micFrame, _pcm16Interleaved);

        return new FrameBuildResult(
            InterleavedPcm16: _pcm16Interleaved,
            SpeakerSamplesRead: speakerRead,
            MicSamplesConsumed: micRead,
            MicSamplesRequested: frameSamples,
            UnderflowSamples: (frameSamples - speakerRead) + (frameSamples - micRead),
            OverflowSamples: 0,
            AppliedPpm: (micRatio - 1d) * 1_000_000d,
            SpeakerLevel: Rms(_speakerFrame),
            MicLevel: Rms(_micFrame));
    }

    private void EnsureCapacity(int frameSamples)
    {
        if (_speakerFrame.Length == frameSamples)
        {
            return;
        }

        _speakerFrame = new float[frameSamples];
        _micRawFrame = new float[frameSamples];
        _micFrame = new float[frameSamples];
        _pcm16Interleaved = new byte[frameSamples * 2 * sizeof(short)];
    }

    private static void InterleaveToPcm16(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<byte> destination)
    {
        for (var i = 0; i < left.Length; i++)
        {
            var l = FloatToInt16(left[i]);
            var r = FloatToInt16(right[i]);

            var o = i * 4;
            destination[o] = (byte)(l & 0xFF);
            destination[o + 1] = (byte)((l >> 8) & 0xFF);
            destination[o + 2] = (byte)(r & 0xFF);
            destination[o + 3] = (byte)((r >> 8) & 0xFF);
        }
    }

    private static short FloatToInt16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0d;
        }

        double sum = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
