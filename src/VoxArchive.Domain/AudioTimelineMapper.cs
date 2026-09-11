namespace VoxArchive.Domain;

/// <summary>
/// Audio Editorの元音声時間軸と、Crossfade適用後の実再生時間軸を相互変換する。
/// </summary>
/// <remarks>
/// Editorの表示時間軸は常に元音声時間を維持する一方、Edited Previewの実ファイルはCutとCrossfadeで短くなる。
/// SeekとPlayhead表示で両者を混同しないため、RenderPlanと同じサンプルフレーム境界を使って変換する。
/// </remarks>
public sealed class AudioTimelineMapper
{
    private readonly AudioEditState _state;
    private readonly AudioRenderPlan _plan;

    public AudioTimelineMapper(AudioEditState state, int sampleRate)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _plan = AudioRenderPlan.Create(state, sampleRate);
    }

    public TimeSpan SourceDuration => _state.SourceDuration;

    public TimeSpan RenderedDuration
    {
        get
        {
            var frames = _plan.KeptFrameCount - _plan.Junctions.Sum(x => (long)x.CrossfadeFrameCount);
            return FrameToTime(Math.Max(0, frames));
        }
    }

    /// <summary>
    /// 元音声時間上の任意Seek先を、Edited Previewで再生可能な位置へ補正する。
    /// </summary>
    public TimeSpan ResolveSourceSeekTarget(TimeSpan requested, AudioSeekDirection direction = AudioSeekDirection.Forward)
    {
        var clamped = ClampSourceTime(requested);
        foreach (var cut in _state.CutRanges)
        {
            if (clamped < cut.Start || clamped >= cut.End) continue;
            return direction == AudioSeekDirection.Backward ? cut.Start : cut.End;
        }
        return clamped;
    }

    /// <summary>
    /// 元音声時間をEdited Previewファイル上の再生位置へ変換する。
    /// CutRange内は方向指定に従って境界へ寄せる。
    /// </summary>
    public TimeSpan SourceToRendered(TimeSpan sourceTime, AudioSeekDirection direction = AudioSeekDirection.Forward)
    {
        var resolved = ResolveSourceSeekTarget(sourceTime, direction);
        var sourceFrame = Math.Clamp(AudioRenderPlan.TimeToFrameIndex(resolved, _plan.SampleRate), 0, _plan.SourceFrameCount);
        if (sourceFrame >= _plan.SourceFrameCount) return RenderedDuration;

        long outputCursor = 0;
        for (var i = 0; i < _plan.KeepRanges.Count; i++)
        {
            var keep = _plan.KeepRanges[i];
            var incoming = i == 0 ? 0 : _plan.Junctions[i - 1].CrossfadeFrameCount;
            var outgoing = i >= _plan.Junctions.Count ? 0 : _plan.Junctions[i].CrossfadeFrameCount;
            var bodyStart = keep.StartFrame + incoming;
            var bodyEnd = keep.EndFrameExclusive - outgoing;

            if (incoming > 0 && sourceFrame >= keep.StartFrame && sourceFrame < bodyStart)
            {
                return FrameToTime(Math.Max(0, outputCursor - incoming + (sourceFrame - keep.StartFrame)));
            }

            if (sourceFrame >= bodyStart && sourceFrame < bodyEnd)
            {
                return FrameToTime(outputCursor + (sourceFrame - bodyStart));
            }

            var bodyLength = Math.Max(0, bodyEnd - bodyStart);
            outputCursor += bodyLength;

            if (outgoing > 0)
            {
                if (sourceFrame >= bodyEnd && sourceFrame < keep.EndFrameExclusive)
                {
                    return FrameToTime(outputCursor + (sourceFrame - bodyEnd));
                }
                outputCursor += outgoing;
            }
        }

        return RenderedDuration;
    }

    /// <summary>
    /// Edited Previewファイル上の位置を元音声時間軸へ戻す。
    /// Crossfade中は接続後側の元音声位置として扱い、PlayheadをCutRange内へ表示しない。
    /// </summary>
    public TimeSpan RenderedToSource(TimeSpan renderedTime)
    {
        var renderedFrame = Math.Clamp(AudioRenderPlan.TimeToFrameIndex(ClampRenderedTime(renderedTime), _plan.SampleRate), 0, Math.Max(0, AudioRenderPlan.TimeToFrameIndex(RenderedDuration, _plan.SampleRate)));
        long outputCursor = 0;

        for (var i = 0; i < _plan.KeepRanges.Count; i++)
        {
            var keep = _plan.KeepRanges[i];
            var incoming = i == 0 ? 0 : _plan.Junctions[i - 1].CrossfadeFrameCount;
            var outgoing = i >= _plan.Junctions.Count ? 0 : _plan.Junctions[i].CrossfadeFrameCount;
            var bodyStart = keep.StartFrame + incoming;
            var bodyEnd = keep.EndFrameExclusive - outgoing;
            var bodyLength = Math.Max(0, bodyEnd - bodyStart);

            if (renderedFrame < outputCursor + bodyLength)
            {
                return FrameToTime(bodyStart + (renderedFrame - outputCursor));
            }
            outputCursor += bodyLength;

            if (outgoing > 0)
            {
                if (renderedFrame < outputCursor + outgoing)
                {
                    var right = _plan.KeepRanges[i + 1];
                    return FrameToTime(right.StartFrame + (renderedFrame - outputCursor));
                }
                outputCursor += outgoing;
            }
        }

        return SourceDuration;
    }

    private TimeSpan ClampSourceTime(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value > SourceDuration ? SourceDuration : value;

    private TimeSpan ClampRenderedTime(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value > RenderedDuration ? RenderedDuration : value;

    private TimeSpan FrameToTime(long frame)
        => TimeSpan.FromSeconds((double)frame / _plan.SampleRate);
}

/// <summary>
/// CutRange内へ相対Seekした場合に寄せる方向を表す。
/// </summary>
public enum AudioSeekDirection
{
    Backward,
    Forward
}
