namespace VoxArchive.Domain;

/// <summary>
/// 音声編集で1チャンネルに適用するGainとMuteの状態を保持する
/// </summary>
public readonly record struct AudioChannelEditState
{
    /// <summary>
    /// チャンネル編集状態を生成する
    /// </summary>
    /// <param name="gainDb">適用するGain。-∞dBから+20dBまでを許可する</param>
    /// <param name="isMuted">ミュート状態</param>
    /// <exception cref="ArgumentOutOfRangeException">Gainが許容範囲外、NaN、正の無限大の場合</exception>
    public AudioChannelEditState(double gainDb, bool isMuted = false)
    {
        if (double.IsNaN(gainDb) || double.IsPositiveInfinity(gainDb) || gainDb > 20d)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), "Gainは-∞dBから+20dBまでで指定する必要があります。");
        }

        GainDb = gainDb;
        IsMuted = isMuted;
    }

    /// <summary>
    /// 適用するGain
    /// </summary>
    public double GainDb { get; }

    /// <summary>
    /// ミュートされているか
    /// </summary>
    public bool IsMuted { get; }

    /// <summary>
    /// デフォルトの0dB・Mute解除状態
    /// </summary>
    public static AudioChannelEditState Default => new(0d);

    /// <summary>
    /// dB値を線形Gainへ変換する
    /// </summary>
    /// <returns>-∞dBでは0、それ以外は対応する線形Gain</returns>
    public double ToLinearGain()
        => double.IsNegativeInfinity(GainDb) ? 0d : Math.Pow(10d, GainDb / 20d);
}
