namespace VoxArchive.Domain;

/// <summary>
/// 元音声時間軸上で書き出し時に除去する区間を表す
/// </summary>
/// <remarks>
/// 編集画面ではカット後の圧縮された時間軸ではなく元音声時間を一貫して使用するため、
/// この型も常に元音声上の開始・終了位置を保持する。
/// </remarks>
public readonly record struct AudioCutRange
{
    /// <summary>
    /// カット区間を生成する
    /// </summary>
    /// <param name="start">元音声時間軸上の開始位置</param>
    /// <param name="end">元音声時間軸上の終了位置</param>
    /// <exception cref="ArgumentOutOfRangeException">開始位置が負数、または終了位置が開始位置以下の場合</exception>
    public AudioCutRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "カット開始位置は0以上である必要があります。");
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "カット終了位置は開始位置より後である必要があります。");
        }

        Start = start;
        End = end;
    }

    /// <summary>
    /// 元音声時間軸上の開始位置
    /// </summary>
    public TimeSpan Start { get; }

    /// <summary>
    /// 元音声時間軸上の終了位置
    /// </summary>
    public TimeSpan End { get; }

    /// <summary>
    /// 除去対象となる論理上の長さ
    /// </summary>
    public TimeSpan Duration => End - Start;
}
