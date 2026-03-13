using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class PiDriftCorrector : IDriftCorrector
{
    private double _kp;
    private double _ki;
    private double _maxCorrection;
    private double _integral;

    public PiDriftCorrector(double kp, double ki, double maxCorrectionPpm)
    {
        Configure(kp, ki, maxCorrectionPpm);
    }

    public double CurrentPpm { get; private set; }

    public void Configure(double kp, double ki, double maxCorrectionPpm)
    {
        if (maxCorrectionPpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCorrectionPpm));
        }

        _kp = kp;
        _ki = ki;
        _maxCorrection = maxCorrectionPpm / 1_000_000d;
    }

    public void Reset()
    {
        _integral = 0;
        CurrentPpm = 0;
    }

    public double ComputeRatio(int micFillSamples, int targetFillSamples, TimeSpan deltaTime)
    {
        var dt = Math.Max(deltaTime.TotalSeconds, 1e-6);
        var error = micFillSamples - targetFillSamples;

        _integral += error * dt;

        var correction = (_kp * error) + (_ki * _integral);
        correction = Math.Clamp(correction, -_maxCorrection, _maxCorrection);

        CurrentPpm = correction * 1_000_000d;
        return 1d + correction;
    }
}
