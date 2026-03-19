namespace VoxArchive.Audio.Abstractions;

public interface IDriftCorrector
{
    double CurrentPpm { get; }
    void Configure(double kp, double ki, double maxCorrectionPpm);
    void Reset();
    double ComputeRatio(int micFillSamples, int targetFillSamples, TimeSpan deltaTime);
}
