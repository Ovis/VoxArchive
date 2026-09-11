namespace VoxArchive.Domain;

/// <summary>
/// Audio Editorの限定範囲確認再生種別を表す。
/// </summary>
public enum AudioAuditionKind
{
    Selection,
    CutOriginal,
    CutBoundary
}

/// <summary>
/// 元音声時間軸上の確認再生範囲と、編集後レンダリングを使用するかを表す。
/// </summary>
/// <remarks>
/// 再生UI固有の状態は保持せず、どの元音声区間をどの再生経路で確認するかだけをDomain側で確定する。
/// </remarks>
public readonly record struct AudioAuditionPlan(
    AudioAuditionKind Kind,
    TimeSpan SourceStart,
    TimeSpan SourceEnd,
    bool UsesEditedTimeline)
{
    public static readonly TimeSpan DefaultBoundaryContext = TimeSpan.FromSeconds(3);

    public TimeSpan SourceDuration => SourceEnd - SourceStart;

    public static AudioAuditionPlan ForSelection(TimeSpan start, TimeSpan end, bool edited)
    {
        ValidateRange(start, end);
        return new AudioAuditionPlan(AudioAuditionKind.Selection, start, end, edited);
    }

    public static AudioAuditionPlan ForCutOriginal(AudioCutRange cut)
        => new(AudioAuditionKind.CutOriginal, cut.Start, cut.End, UsesEditedTimeline: false);

    public static AudioAuditionPlan ForCutBoundary(
        AudioCutRange cut,
        TimeSpan sourceDuration,
        TimeSpan? context = null)
    {
        if (sourceDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        }

        var margin = context ?? DefaultBoundaryContext;
        if (margin < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        var start = cut.Start - margin;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        var end = cut.End + margin;
        if (end > sourceDuration) end = sourceDuration;
        ValidateRange(start, end);
        return new AudioAuditionPlan(AudioAuditionKind.CutBoundary, start, end, UsesEditedTimeline: true);
    }

    private static void ValidateRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }
    }
}
