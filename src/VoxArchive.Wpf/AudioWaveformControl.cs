using System.Diagnostics;
using System.Globalization;
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
    private const double ChannelLabelWidth = 74d;
    private const double TimeRulerHeight = 24d;
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(15, 23, 35));
    private static readonly Brush Channel1Brush = new SolidColorBrush(Color.FromRgb(67, 148, 255));
    private static readonly Brush Channel2Brush = new SolidColorBrush(Color.FromRgb(65, 190, 128));
    private static readonly Brush LabelBackgroundBrush = new SolidColorBrush(Color.FromRgb(17, 29, 43));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(220, 231, 246));
    private static readonly Brush MutedTextBrush = new SolidColorBrush(Color.FromRgb(149, 167, 192));
    private static readonly Brush CenterLineBrush = new SolidColorBrush(Color.FromRgb(48, 65, 88));
    private static readonly Brush RulerLineBrush = new SolidColorBrush(Color.FromRgb(54, 73, 98));
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
    /// 再生中のPlayheadだけを更新する。
    /// </summary>
    /// <remarks>
    /// CutRangeやSelectionの再構築を避け、再生タイマーからの更新を最小限に留める。
    /// </remarks>
    public void SetPlayhead(TimeSpan playhead)
    {
        if (_playhead == playhead) return;
        _playhead = playhead;
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
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || ActualWidth <= ChannelLabelWidth + 1 || ActualHeight <= TimeRulerHeight + 1) return;

        var plot = GetPlotRect();
        dc.DrawRectangle(LabelBackgroundBrush, null, new Rect(0, 0, ChannelLabelWidth, plot.Height));
        dc.DrawRectangle(LabelBackgroundBrush, null, new Rect(0, plot.Bottom, ActualWidth, TimeRulerHeight));

        DrawWaveforms(dc, plot);
        DrawCutRanges(dc, plot);
        DrawSelection(dc, plot);
        DrawPlayhead(dc, plot);
        DrawChannelLabels(dc, plot);
        DrawTimeRuler(dc, plot);
    }

    private Rect GetPlotRect()
        => new(ChannelLabelWidth, 0, Math.Max(1d, ActualWidth - ChannelLabelWidth), Math.Max(1d, ActualHeight - TimeRulerHeight));

    private void DrawWaveforms(DrawingContext dc, Rect plot)
    {
        var channels = _analysis!.Channels;
        var trackHeight = plot.Height / channels;
        var detailUsable = _detail is not null && NearlySameViewport(_detail.Viewport, _viewport);
        for (var channel = 0; channel < channels; channel++)
        {
            var center = plot.Top + (channel * trackHeight) + (trackHeight / 2d);
            dc.DrawLine(new Pen(CenterLineBrush, 1d), new Point(plot.Left, center), new Point(plot.Right, center));
            if (channel > 0)
                dc.DrawLine(new Pen(RulerLineBrush, 1d), new Point(0, plot.Top + channel * trackHeight), new Point(plot.Right, plot.Top + channel * trackHeight));

            var envelope = detailUsable ? _detail!.Envelopes[channel] : _analysis.Envelopes[channel];
            var bucketCount = Math.Min(envelope.Minimums.Length, envelope.Maximums.Length);
            if (bucketCount == 0) continue;
            var firstBucket = detailUsable ? 0 : TimeToCoarseBucket(_viewport.Start, bucketCount);
            var lastBucket = detailUsable ? bucketCount - 1 : TimeToCoarseBucket(_viewport.End, bucketCount);
            var visibleBuckets = Math.Max(1, lastBucket - firstBucket + 1);
            var amplitudeHeight = Math.Max(1d, (trackHeight / 2d) - 8d);
            var pen = new Pen(channel == 0 ? Channel1Brush : Channel2Brush, 1d);
            for (var i = 0; i < visibleBuckets; i++)
            {
                var bucket = Math.Min(lastBucket, firstBucket + i);
                var x = visibleBuckets == 1 ? plot.Left : plot.Left + i * plot.Width / (visibleBuckets - 1d);
                var min = Math.Clamp(envelope.Minimums[bucket], -1.25f, 1.25f);
                var max = Math.Clamp(envelope.Maximums[bucket], -1.25f, 1.25f);
                dc.DrawLine(pen, new Point(x, center - (max * amplitudeHeight)), new Point(x, center - (min * amplitudeHeight)));
            }
        }
    }

    private void DrawChannelLabels(DrawingContext dc, Rect plot)
    {
        var channels = _analysis!.Channels;
        var trackHeight = plot.Height / channels;
        for (var channel = 0; channel < channels; channel++)
        {
            var top = plot.Top + channel * trackHeight;
            DrawText(dc, channel == 0 ? "CH1" : "CH2", 12d, FontWeights.SemiBold, TextBrush, new Point(10, top + 12));
            if (channels > 1)
                DrawText(dc, channel == 0 ? "Speaker" : "Microphone", 10d, FontWeights.Normal, MutedTextBrush, new Point(10, top + 31));
        }
        dc.DrawLine(new Pen(RulerLineBrush, 1d), new Point(ChannelLabelWidth, 0), new Point(ChannelLabelWidth, plot.Bottom));
    }

    private void DrawTimeRuler(DrawingContext dc, Rect plot)
    {
        const int divisions = 6;
        dc.DrawLine(new Pen(RulerLineBrush, 1d), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        for (var i = 0; i <= divisions; i++)
        {
            var ratio = i / (double)divisions;
            var x = plot.Left + plot.Width * ratio;
            var time = _viewport.Start + TimeSpan.FromTicks((long)Math.Round(_viewport.Duration.Ticks * ratio));
            dc.DrawLine(new Pen(RulerLineBrush, 1d), new Point(x, plot.Bottom), new Point(x, plot.Bottom + 5));
            var label = FormatRulerTime(time);
            var text = CreateText(label, 10d, FontWeights.Normal, MutedTextBrush);
            var textX = Math.Clamp(x - text.Width / 2d, plot.Left + 2, Math.Max(plot.Left + 2, plot.Right - text.Width - 2));
            dc.DrawText(text, new Point(textX, plot.Bottom + 6));
        }
    }

    private static string FormatRulerTime(TimeSpan time)
        => time.TotalHours >= 1d ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";

    private void DrawText(DrawingContext dc, string value, double size, FontWeight weight, Brush brush, Point origin)
        => dc.DrawText(CreateText(value, size, weight, brush), origin);

    private FormattedText CreateText(string value, double size, FontWeight weight, Brush brush)
        => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int TimeToCoarseBucket(TimeSpan time, int bucketCount)
    {
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || bucketCount <= 1) return 0;
        var ratio = Math.Clamp(time.TotalSeconds / _analysis.Duration.TotalSeconds, 0d, 1d);
        return (int)Math.Round(ratio * (bucketCount - 1));
    }

    private void DrawCutRanges(DrawingContext dc, Rect plot)
    {
        foreach (var cut in _cuts)
        {
            if (cut.End < _viewport.Start || cut.Start > _viewport.End) continue;
            var x1 = TimeToX(cut.Start);
            var x2 = TimeToX(cut.End);
            var selected = _selectedCut.HasValue && _selectedCut.Value.Equals(cut);
            dc.DrawRectangle(selected ? SelectedCutBrush : CutBrush, null, new Rect(x1, plot.Top, Math.Max(1d, x2 - x1), plot.Height));
            if (selected)
            {
                dc.DrawLine(BoundaryPen, new Point(x1, plot.Top), new Point(x1, plot.Bottom));
                dc.DrawLine(BoundaryPen, new Point(x2, plot.Top), new Point(x2, plot.Bottom));
            }
        }
    }

    private void DrawSelection(DrawingContext dc, Rect plot)
    {
        if (!_selectionStart.HasValue || !_selectionEnd.HasValue || _selectionEnd <= _selectionStart) return;
        var x1 = TimeToX(_selectionStart.Value);
        var x2 = TimeToX(_selectionEnd.Value);
        dc.DrawRectangle(SelectionBrush, null, new Rect(x1, plot.Top, Math.Max(1d, x2 - x1), plot.Height));
    }

    private void DrawPlayhead(DrawingContext dc, Rect plot)
    {
        if (_playhead < _viewport.Start || _playhead > _viewport.End) return;
        var x = TimeToX(_playhead);
        dc.DrawLine(PlayheadPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
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
        var position = e.GetPosition(this);
        var plot = GetPlotRect();
        if (!plot.Contains(position)) return;

        Focus();
        CaptureMouse();
        _pointerDown = true;
        _selecting = false;
        _draggingBoundary = false;
        _pointerDownPoint = position;
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

        // Drag中はControl内部のvisualだけを更新する。
        // ViewModelへMouseMoveごとに通知するとPropertyChanged経由で波形全体が再設定され、入力追従性を損なう。
        InvalidateVisual();
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

                // Smoke Test中だけ、Waveform clickからWindow側Seek処理が戻るまでの同期経路を計測する。
                // PreviewService.Seek自体が重いのか、UI再描画側が重いのかを切り分けるための一時診断である。
                var seekStarted = Stopwatch.GetTimestamp();
                App.WriteAudioEditorDiagnostic($"Waveform seek requested. TargetMs={current.TotalMilliseconds:F0}");
                SeekRequested?.Invoke(this, new AudioWaveformSeekRequestedEventArgs(current));
                var elapsed = Stopwatch.GetElapsedTime(seekStarted);
                App.WriteAudioEditorDiagnostic($"Waveform seek handler completed. TargetMs={current.TotalMilliseconds:F0}, HandlerElapsedMs={elapsed.TotalMilliseconds:F2}");
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
        var plot = GetPlotRect();
        if (_viewport.Duration <= TimeSpan.Zero) return plot.Left;
        return plot.Left + Math.Clamp((time - _viewport.Start).TotalSeconds / _viewport.Duration.TotalSeconds, 0d, 1d) * plot.Width;
    }

    private TimeSpan XToTime(double x)
    {
        if (_analysis is null || _viewport.Duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var plot = GetPlotRect();
        var ratio = Math.Clamp((x - plot.Left) / plot.Width, 0d, 1d);
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