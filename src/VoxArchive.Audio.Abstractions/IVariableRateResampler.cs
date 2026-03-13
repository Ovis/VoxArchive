namespace VoxArchive.Audio.Abstractions;

public interface IVariableRateResampler
{
    void Reset();
    int Resample(ReadOnlySpan<float> source, Span<float> destination, double ratio, out int consumedSamples);
}
