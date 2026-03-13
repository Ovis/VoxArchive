namespace VoxArchive.Audio.Abstractions;

public interface IDriftCorrector
{
    double CurrentPpm { get; }
    void Reset();
    double ComputeRatio(int micFillSamples, int targetFillSamples, TimeSpan deltaTime);
}
