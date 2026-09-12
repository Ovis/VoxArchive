namespace VoxArchive.Domain;

/// <summary>
/// 編集後音声の実ピークと、指定ターゲットへ収めるためのMaster減衰量を表す。
/// </summary>
public readonly record struct AudioPeakAssessment(
    double PeakAbsoluteSample,
    double PeakDbfs,
    bool IsClipping,
    double TargetPeakDbfs,
    double RequiredMasterGainDb)
{
    public const double DefaultTargetPeakDbfs = -0.1d;

    /// <summary>
    /// 実サンプルピークからClipping状態と必要なMaster減衰量を算出する。
    /// </summary>
    public static AudioPeakAssessment FromPeak(
        double peakAbsoluteSample,
        double targetPeakDbfs = DefaultTargetPeakDbfs)
    {
        if (double.IsNaN(peakAbsoluteSample) || double.IsInfinity(peakAbsoluteSample) || peakAbsoluteSample < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(peakAbsoluteSample));
        }

        if (double.IsNaN(targetPeakDbfs) || double.IsInfinity(targetPeakDbfs) || targetPeakDbfs > 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPeakDbfs));
        }

        var peakDbfs = peakAbsoluteSample <= 0d
            ? double.NegativeInfinity
            : 20d * Math.Log10(peakAbsoluteSample);
        var targetLinear = Math.Pow(10d, targetPeakDbfs / 20d);
        var requiredGain = peakAbsoluteSample <= targetLinear || peakAbsoluteSample <= 0d
            ? 0d
            : 20d * Math.Log10(targetLinear / peakAbsoluteSample);

        return new AudioPeakAssessment(
            peakAbsoluteSample,
            peakDbfs,
            peakAbsoluteSample > 1d,
            targetPeakDbfs,
            requiredGain);
    }
}
