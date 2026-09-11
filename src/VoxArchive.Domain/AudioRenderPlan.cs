namespace VoxArchive.Domain;

/// <summary>
/// 書き出し時のチャンネル構成を表す
/// </summary>
public enum AudioRenderChannelMode
{
    /// <summary>
    /// Gain・Mute適用後の各チャンネルを加算してモノラル化する
    /// </summary>
    MonoMixdown,

    /// <summary>
    /// 元のStereo構成を維持する。Mono入力では1チャンネルのまま扱う
    /// </summary>
    Stereo
}

/// <summary>
/// 元音声上で残すサンプルフレーム範囲を表す
/// </summary>
public readonly record struct AudioFrameRange(long StartFrame, long EndFrameExclusive)
{
    public long Length => EndFrameExclusive - StartFrame;
}

/// <summary>
/// Cut後に接続される2区間とCrossfade長を表す
/// </summary>
public readonly record struct AudioRenderJunction(
    AudioFrameRange Left,
    AudioFrameRange Right,
    int CrossfadeFrameCount);

/// <summary>
/// CutRangeをサンプルフレーム境界へ投影したストリーミング向けレンダリング計画
/// </summary>
/// <remarks>
/// PCM全体を保持せず、後続のReader/WriterがこのKeepRangesを順番に処理できるようにする。
/// Crossfadeは仕様上5msを基準とし、接続するどちらかの区間が短い場合は短い側に合わせて縮める。
/// </remarks>
public sealed class AudioRenderPlan
{
    public const double DefaultCrossfadeMilliseconds = 5d;

    private AudioRenderPlan(
        int sampleRate,
        long sourceFrameCount,
        IReadOnlyList<AudioFrameRange> keepRanges,
        IReadOnlyList<AudioRenderJunction> junctions)
    {
        SampleRate = sampleRate;
        SourceFrameCount = sourceFrameCount;
        KeepRanges = keepRanges;
        Junctions = junctions;
    }

    public int SampleRate { get; }

    public long SourceFrameCount { get; }

    public IReadOnlyList<AudioFrameRange> KeepRanges { get; }

    public IReadOnlyList<AudioRenderJunction> Junctions { get; }

    /// <summary>
    /// Crossfadeによるオーバーラップを考慮しない、Cut適用後のフレーム数
    /// </summary>
    public long KeptFrameCount => KeepRanges.Sum(x => x.Length);

    public static AudioRenderPlan Create(AudioEditState state, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var sourceFrameCount = TimeToFrameIndex(state.SourceDuration, sampleRate);
        var cuts = state.CutRanges
            .Select(x => new AudioFrameRange(
                TimeToFrameIndex(x.Start, sampleRate),
                TimeToFrameIndex(x.End, sampleRate)))
            .Where(x => x.Length > 0)
            .ToArray();

        var keep = new List<AudioFrameRange>(cuts.Length + 1);
        long cursor = 0;
        foreach (var cut in cuts)
        {
            if (cut.StartFrame > cursor)
            {
                keep.Add(new AudioFrameRange(cursor, cut.StartFrame));
            }

            cursor = Math.Max(cursor, cut.EndFrameExclusive);
        }

        if (cursor < sourceFrameCount)
        {
            keep.Add(new AudioFrameRange(cursor, sourceFrameCount));
        }

        var requestedCrossfadeFrames = Math.Max(
            1,
            (int)Math.Round(sampleRate * DefaultCrossfadeMilliseconds / 1000d, MidpointRounding.AwayFromZero));
        var junctions = new List<AudioRenderJunction>(Math.Max(0, keep.Count - 1));
        for (var i = 0; i + 1 < keep.Count; i++)
        {
            var left = keep[i];
            var right = keep[i + 1];
            var fadeFrames = (int)Math.Min(requestedCrossfadeFrames, Math.Min(left.Length, right.Length));
            junctions.Add(new AudioRenderJunction(left, right, fadeFrames));
        }

        return new AudioRenderPlan(sampleRate, sourceFrameCount, keep, junctions);
    }

    /// <summary>
    /// TimeSpanを最も近いサンプルフレーム境界へ変換する
    /// </summary>
    public static long TimeToFrameIndex(TimeSpan time, int sampleRate)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        checked
        {
            var numerator = time.Ticks * (long)sampleRate;
            return (numerator + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond;
        }
    }
}
