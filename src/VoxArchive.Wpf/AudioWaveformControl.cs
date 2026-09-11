using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorの波形表示、Viewport操作、Selection、Playhead、CutRange操作を提供する。
/// </summary>
public sealed class AudioWaveformControl : FrameworkElement
{
    private const double DragThreshold = 4d;
    private const double BoundaryHitWidth = 7d;
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(15, 23, 35));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(67, 148, 255));
    private static readonly Brush CenterLineBrush = new SolidColorBrush(Color.FromRgb(48, 65, 88));
    private static readonly Brush CutBrush = new SolidColorBrush(Color.FromArgb(110, 181, 58, 72));
    private static readonly Brush SelectedCutBrush = new SolidColorBrush(Color.FromArgb(155, 205, 70, 84));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(85, 91, 155, 255));
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromRgb(255, 200, 76)), 1.5d);
    private static readonly Pen BoundaryPen = new(new SolidColorBrush(Color.FromRgb(255, 232, 153)), 2d);
    private static readonly Pen PlayAreaBorderPen = new(new SolidColorBrush(Color.FromRgb(45, 63, 86)), 1d);

    private AudioWaveformAnalysisResult? _analysis;
    private AudioWaveformDetailResult? _detail;
    private IReadOnlyList<AudioCutRange> _cuts = Array.Empty<AudioCutRange>();
    private AudioCutRange? _selectedCut;
    private TimeSpan? _selectionStart;
    private TimeSpan? _selectionEnd;
    private TimeSpan _playhead;
    private AudioWaveformViewport _viewport;
    private bool _viewportInitialized;
    private bool _pointerDown;
    private bool _selecting;
    private bool _draggingBoundary;
    private bool _draggingStartBoundary;
    private Point _pointerDownPoint;
    private TimeSpan _selectionAnchor;

    public event EventHandler<AudioWaveformSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<AudioWaveformSeekRequestedEventArgs>? SeekRequested;
    public event EventHandler<AudioWaveformViewportRequestedEventArgs>? ViewportRequested;
    public event EventHandler<AudioWaveformCutRangeSelectedEventArgs>? CutRangeSelected;
    public event EventHandler<AudioWaveformCutBoundaryChangedEventArgs>? CutBoundaryChanged;
    public event EventHandler? TimelineEditStarted;

    public AudioWaveformControl()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    /// <summary>
    /// 既存Editorの状態更新経路。Viewportは維持し、編集状態だけを差し替える。
    /// </summary>
    public void SetContent(
        AudioWaveformAnalysisResult? analysis,
        IReadOnlyList<AudioCutRange>? cuts,
        TimeSpan? selectionStart,
        TimeSpan? selectionEnd,
        TimeSpan playhead)
    {
        _analysis = analysis;
        _cuts = cuts ?? Array.Empty<AudioCutRange>();
        _selectionStart = selectionStart;
        _selectionEnd = selectionEnd;
        _playhead = playhead;
        if (analysis is not null && (!_viewportInitialized || _viewport.Duration <= TimeSpan.Zero))
        {
            _viewport = AudioWaveformViewport.Full(analysis.Duration);
            _viewportInitialized = true;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Zoom/Scrollと高解像度波形、CutRange選択状態を更新する。
    /// </summary>
    public void SetViewportState(AudioWaveformViewport viewport, AudioWaveformDetailResult? detail, AudioCutRange? selectedCut)
    {
        _viewport = viewport;
        _viewportInitialized = true;
        _detail = detail;
        _selectedCut = selectedCut;
        InvalidateVisual();
    }

    public AudioWaveformViewport CurrentViewport => _viewport;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        dc.DrawRectangle(null, PlayAreaBorderPen, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || ActualWidth <= 1 || ActualHeight <= 1) return;
        DrawWaveforms(dc);
        DrawCutRanges(dc);
        DrawSelection(dc);
        DrawPlayhead(dc);
    }

    private void DrawWaveforms(DrawingContext dc)
    {
        var channels = _analysis!.Channels;
        var trackHeight = ActualHeight / channels;
        var pen = new Pen(TrackBrush, 1d);
        var detailUsable = _detail is not null && NearlySameViewport(_detail.Viewport, _viewport);
        for (var channel = 0; channel < channels; channel++)
        {
            var center = (channel * trackHeight) + (trackHeight / 2d);
            dc.DrawLine(new Pen(CenterLineBrush, 1d), new Point(0, center), new Point(ActualWidth, center));
            var envelope = detailUsable ? _detail!.Envelopes[channel] : _analysis.Envelopes[channel];
            var bucketCount = Math.Min(envelope.Minimums.Length, envelope.Maximums.Length);
            if (bucketCount == 0) continue;
            var firstBucket = detailUsable ? 0 : TimeToCoarseBucket(_viewport.Start, bucketCount);
            var lastBucket = detailUsable ? bucketCount - 1 : TimeToCoarseBucket(_viewport.End, bucketCount);
            var visibleBuckets = Math.Max(1, lastBucket - firstBucket + 1);
            var amplitudeHeight = Math.Max(1d, (trackHeight / 2d) - 8d);
            for (var i = 0; i < visibleBuckets; i++)
            {
                var bucket = Math.Min(lastBucket, firstBucket + i);
                var x = visibleBuckets == 1 ? 0d : i * ActualWidth / (visibleBuckets - 1d);
                var min = Math.Clamp(envelope.Minimums[bucket], -1.25f, 1.25f);
                var max = Math.Clamp(envelope.Maximums[bucket], -1.25f, 1.25f);
                dc.DrawLine(pen, new Point(x, center - (max * amplitudeHeight)), new Point(x, center - (min * amplitudeHeight)));
            }
        }
    }

    private int TimeToCoarseBucket(TimeSpan time, int bucketCount)
    {
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || bucketCount <= 1) return 0;
        var ratio = Math.Clamp(time.TotalSeconds / _analysis.Duration.TotalSeconds, 0d, 1d);
        return (int)Math.Round(ratio * (bucketCount - 1));
    }

    private void DrawCutRanges(DrawingContext dc)
    {
        foreach (var cut in _cuts)
        {
            if (cut.End < _viewport.Start || cut.Start > _viewport.End) continue;
            var x1 = TimeToX(cut.Start);
            var x2 = TimeToX(cut.End);
            var selected = _selectedCut.HasValue && _selectedCut.Value.Equals(cut);
            dc.DrawRectangle(selected ? SelectedCutBrush : CutBrush, null, new Rect(x1, 0, Math.Max(1d, x2 - x1), ActualHeight));
            if (selected)
            {
                dc.DrawLine(BoundaryPen, new Point(x1, 0), new Point(x1, ActualHeight));
                dc.DrawLine(BoundaryPen, new Point(x2, 0), new Point(x2, ActualHeight));
            }
        }
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (!_selectionStart.HasValue || !_selectionEnd.HasValue || _selectionEnd <= _selectionStart) return;
        var x1 = TimeToX(_selectionStart.Value);
        var x2 = TimeToX(_selectionEnd.Value);
        dc.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, Math.Max(1d, x2 - x1), ActualHeight));
    }

    private void DrawPlayhead(DrawingContext dc)
    {
        if (_playhead < _viewport.Start || _playhead > _viewport.End) return;
        var x = TimeToX(_playhead);
        dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, ActualHeight));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero) return;
        var anchor = XToTime(e.GetPosition(this).X);
        var factor = e.Delta > 0 ? 1.35d : 1d / 1.35d;
        var next = _viewport.ZoomAround(anchor, factor, _analysis.Duration, TimeSpan.FromMilliseconds(50));
        ViewportRequested?.Invoke(this, new AudioWaveformViewportRequestedEventArgs(next, true));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero) return;
        Focus();
        CaptureMouse();
        _pointerDown = true;
        _selecting = false;
        _draggingBoundary = false;
        _pointerDownPoint = e.GetPosition(this);
        _selectionAnchor = XToTime(_pointerDownPoint.X);
        if (_selectedCut.HasValue)
        {
            var startX = TimeToX(_selectedCut.Value.Start);
            var endX = TimeToX(_selectedCut.Value.End);
            if (Math.Abs(_pointerDownPoint.X - startX) <= BoundaryHitWidth || Math.Abs(_pointerDownPoint.X - endX) <= BoundaryHitWidth)
            {
                _draggingBoundary = true;
                _draggingStartBoundary = Math.Abs(_pointerDownPoint.X - startX) <= Math.Abs(_pointerDownPoint.X - endX);
                TimelineEditStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (_draggingBoundary && _selectedCut.HasValue)
        {
            var time = XToTime(point.X);
            var original = _selectedCut.Value;
            var start = _draggingStartBoundary ? time : original.Start;
            var end = _draggingStartBoundary ? original.End : time;
            if (end > start)
            {
                _selectedCut = new AudioCutRange(start, end);
                InvalidateVisual();
                CutBoundaryChanged?.Invoke(this, new AudioWaveformCutBoundaryChangedEventArgs(original, start, end, false));
            }
            return;
        }
        if (!_selecting && Math.Abs(point.X - _pointerDownPoint.X) >= DragThreshold)
        {
            _selecting = true;
            TimelineEditStarted?.Invoke(this, EventArgs.Empty);
            _selectionStart = _selectionAnchor;
            _selectionEnd = _selectionAnchor;
        }
        if (!_selecting) return;
        var current = XToTime(point.X);
        _selectionStart = current < _selectionAnchor ? current : _selectionAnchor;
        _selectionEnd = current < _selectionAnchor ? _selectionAnchor : current;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, new AudioWaveformSelectionChangedEventArgs(_selectionStart.Value, _selectionEnd.Value, false));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pointerDown) return;
        var current = XToTime(e.GetPosition(this).X);
        _pointerDown = false;
        ReleaseMouseCapture();
        if (_draggingBoundary && _selectedCut.HasValue)
        {
            var visual = _selectedCut.Value;
            _draggingBoundary = false;
            CutBoundaryChanged?.Invoke(this, new AudioWaveformCutBoundaryChangedEventArgs(visual, visual.Start, visual.End, true));
            e.Handled = true;
            return;
        }
        if (_selecting)
        {
            _selectionStart = current < _selectionAnchor ? current : _selectionAnchor;
            _selectionEnd = current < _selectionAnchor ? _selectionAnchor : current;
            _selecting = false;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, new AudioWaveformSelectionChangedEventArgs(_selectionStart.Value, _selectionEnd.Value, true));
        }
        else
        {
            var cut = HitTestCutRange(current);
            if (cut.HasValue)
            {
                CutRangeSelected?.Invoke(this, new AudioWaveformCutRangeSelectedEventArgs(cut.Value));
            }
            else
            {
                _selectionStart = null;
                _selectionEnd = null;
                _playhead = current;
                InvalidateVisual();
                CutRangeSelected?.Invoke(this, new AudioWaveformCutRangeSelectedEventArgs(null));
                SeekRequested?.Invoke(this, new AudioWaveformSeekRequestedEventArgs(current));
            }
        }
        e.Handled = true;
    }

    private AudioCutRange? HitTestCutRange(TimeSpan time)
    {
        foreach (var cut in _cuts) if (time >= cut.Start && time <= cut.End) return cut;
        return null;
    }

    private double TimeToX(TimeSpan time)
    {
        if (_viewport.Duration <= TimeSpan.Zero) return 0d;
        return Math.Clamp((time - _viewport.Start).TotalSeconds / _viewport.Duration.TotalSeconds, 0d, 1d) * ActualWidth;
    }

    private TimeSpan XToTime(double x)
    {
        if (_analysis is null || _viewport.Duration <= TimeSpan.Zero || ActualWidth <= 0) return TimeSpan.Zero;
        var ratio = Math.Clamp(x / ActualWidth, 0d, 1d);
        return _viewport.Start + TimeSpan.FromTicks((long)Math.Round(_viewport.Duration.Ticks * ratio));
    }

    private static bool NearlySameViewport(AudioWaveformViewport left, AudioWaveformViewport right)
        => Math.Abs((left.Start - right.Start).TotalMilliseconds) <= 15d && Math.Abs((left.End - right.End).TotalMilliseconds) <= 15d;
}

public sealed class AudioWaveformSelectionChangedEventArgs(TimeSpan start, TimeSpan end, bool isFinal) : EventArgs
{
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public bool IsFinal { get; } = isFinal;
}

public sealed class AudioWaveformSeekRequestedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}

public sealed class AudioWaveformViewportRequestedEventArgs(AudioWaveformViewport viewport, bool manual) : EventArgs
{
    public AudioWaveformViewport Viewport { get; } = viewport;
    public bool Manual { get; } = manual;
}

public sealed class AudioWaveformCutRangeSelectedEventArgs(AudioCutRange? range) : EventArgs
{
    public AudioCutRange? Range { get; } = range;
}

public sealed class AudioWaveformCutBoundaryChangedEventArgs(AudioCutRange visualRange, TimeSpan start, TimeSpan end, bool isFinal) : EventArgs
{
    public AudioCutRange VisualRange { get; } = visualRange;
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public bool IsFinal { get; } = isFinal;
}
