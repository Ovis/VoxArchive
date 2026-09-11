namespace VoxArchive.Domain;

/// <summary>
/// 元音声時間軸上の波形表示範囲を表す。
/// </summary>
/// <remarks>
/// Zoom/Scroll/Followは編集状態ではないため、CutRange等のDomain状態とは分離して軽量な値として扱う。
/// </remarks>
public readonly record struct AudioWaveformViewport(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public static AudioWaveformViewport Full(TimeSpan sourceDuration)
        => Normalize(TimeSpan.Zero, sourceDuration, sourceDuration);

    public static AudioWaveformViewport Normalize(TimeSpan start, TimeSpan end, TimeSpan sourceDuration)
    {
        if (sourceDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        if (sourceDuration == TimeSpan.Zero) return new AudioWaveformViewport(TimeSpan.Zero, TimeSpan.Zero);

        start = Clamp(start, TimeSpan.Zero, sourceDuration);
        end = Clamp(end, TimeSpan.Zero, sourceDuration);
        if (end < start) (start, end) = (end, start);
        if (end == start)
        {
            var oneTick = TimeSpan.FromTicks(1);
            end = start < sourceDuration ? start + oneTick : sourceDuration;
            start = end == sourceDuration ? sourceDuration - oneTick : start;
        }
        return new AudioWaveformViewport(start, end);
    }

    public AudioWaveformViewport ZoomAround(TimeSpan anchor, double factor, TimeSpan sourceDuration, TimeSpan minimumDuration)
    {
        if (factor <= 0d || double.IsNaN(factor) || double.IsInfinity(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        if (sourceDuration <= TimeSpan.Zero) return Full(sourceDuration);
        if (minimumDuration <= TimeSpan.Zero) minimumDuration = TimeSpan.FromMilliseconds(10);

        anchor = Clamp(anchor, Start, End);
        var currentTicks = Math.Max(1L, Duration.Ticks);
        var targetTicks = (long)Math.Round(currentTicks / factor, MidpointRounding.AwayFromZero);
        targetTicks = Math.Clamp(targetTicks, Math.Min(minimumDuration.Ticks, sourceDuration.Ticks), sourceDuration.Ticks);
        var ratio = Duration.Ticks <= 0 ? 0.5d : (double)(anchor - Start).Ticks / Duration.Ticks;
        var startTicks = anchor.Ticks - (long)Math.Round(targetTicks * ratio, MidpointRounding.AwayFromZero);
        var endTicks = startTicks + targetTicks;

        if (startTicks < 0)
        {
            endTicks -= startTicks;
            startTicks = 0;
        }
        if (endTicks > sourceDuration.Ticks)
        {
            startTicks -= endTicks - sourceDuration.Ticks;
            endTicks = sourceDuration.Ticks;
        }
        startTicks = Math.Max(0, startTicks);
        return Normalize(TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(endTicks), sourceDuration);
    }

    public AudioWaveformViewport ScrollBy(TimeSpan delta, TimeSpan sourceDuration)
    {
        if (sourceDuration <= TimeSpan.Zero || Duration >= sourceDuration) return Full(sourceDuration);
        var start = Start + delta;
        var end = End + delta;
        if (start < TimeSpan.Zero)
        {
            end -= start;
            start = TimeSpan.Zero;
        }
        if (end > sourceDuration)
        {
            start -= end - sourceDuration;
            end = sourceDuration;
        }
        return Normalize(start, end, sourceDuration);
    }

    public AudioWaveformViewport EnsureVisible(TimeSpan position, TimeSpan sourceDuration, double preferredRatio = 0.5d)
    {
        position = Clamp(position, TimeSpan.Zero, sourceDuration);
        if (position >= Start && position <= End) return this;
        preferredRatio = Math.Clamp(preferredRatio, 0d, 1d);
        var start = position - TimeSpan.FromTicks((long)Math.Round(Duration.Ticks * preferredRatio));
        return Normalize(start, start + Duration, sourceDuration).ScrollIntoBounds(sourceDuration);
    }

    public AudioWaveformViewport PageForward(TimeSpan sourceDuration, double overlapRatio = 0.08d)
    {
        overlapRatio = Math.Clamp(overlapRatio, 0d, 0.5d);
        var shift = TimeSpan.FromTicks((long)Math.Round(Duration.Ticks * (1d - overlapRatio)));
        return ScrollBy(shift, sourceDuration);
    }

    private AudioWaveformViewport ScrollIntoBounds(TimeSpan sourceDuration)
    {
        var start = Start;
        var end = End;
        if (start < TimeSpan.Zero)
        {
            end -= start;
            start = TimeSpan.Zero;
        }
        if (end > sourceDuration)
        {
            start -= end - sourceDuration;
            end = sourceDuration;
        }
        return Normalize(start, end, sourceDuration);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
        => value < minimum ? minimum : value > maximum ? maximum : value;
}
