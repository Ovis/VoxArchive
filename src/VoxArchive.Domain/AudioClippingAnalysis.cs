namespace VoxArchive.Domain;

/// <summary>
/// Gain・Mute・Mixdown適用後のピークを解析し、Clipping防止に必要なMaster減衰量を算出する
/// </summary>
public sealed class AudioClippingAnalysis
{
    private double _peakAbsoluteSample;

    /// <summary>
    /// 現在までに観測した絶対ピーク値
    /// </summary>
    public double PeakAbsoluteSample => _peakAbsoluteSample;

    /// <summary>
    /// 0dBFSを超えるサンプルが存在するか
    /// </summary>
    public bool IsClipping => _peakAbsoluteSample > 1d;

    /// <summary>
    /// 解析時のピークを1.0以下へ収めるために必要なMaster Gain。減衰不要なら0dB
    /// </summary>
    public double RequiredMasterGainDb
        => IsClipping ? -20d * Math.Log10(_peakAbsoluteSample) : 0d;

    /// <summary>
    /// 解析対象サンプルを追加する
    /// </summary>
    public void Observe(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var absolute = Math.Abs((double)sample);
            if (absolute > _peakAbsoluteSample)
            {
                _peakAbsoluteSample = absolute;
            }
        }
    }

    /// <summary>
    /// 利用者が指定したMaster Gainを尊重しつつ、Clippingする場合だけ必要な上限まで下げる
    /// </summary>
    /// <remarks>
    /// 解析値はMaster Gain適用前を前提とする。そのため自動補正は現在値へ減衰量を加算するのではなく、
    /// 「現在値」と「安全な最大値」の小さい方を採用する。
    /// </remarks>
    public double GetSafeMasterGainDb(double currentMasterGainDb = 0d)
    {
        if (double.IsNaN(currentMasterGainDb) || double.IsPositiveInfinity(currentMasterGainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(currentMasterGainDb));
        }

        return Math.Min(currentMasterGainDb, RequiredMasterGainDb);
    }
}
