using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editor向けの全体波形表示と元音声時間軸上の範囲選択を提供する。
/// </summary>
public sealed class AudioWaveformControl : FrameworkElement
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(15, 23, 35));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(67, 148, 255));
    private static readonly Brush CenterLineBrush = new SolidColorBrush(Color.FromRgb(48, 65, 88));
    private static readonly Brush CutBrush = new SolidColorBrush(Color.FromArgb(110, 181, 58, 72));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(85, 91, 155, 255));
    private static readonly Pen PlayAreaBorderPen = new(new SolidColorBrush(Color.FromRgb(45, 63, 86)), 1d);

    private AudioWaveformAnalysisResult? _analysis;
    private IReadOnlyList<AudioCutRange> _cuts = Array.Empty<AudioCutRange>();
    private TimeSpan? _selectionStart;
    private TimeSpan? _selectionEnd;
    private bool _selecting;
    private TimeSpan _selectionAnchor;

    public event EventHandler<AudioWaveformSelectionChangedEventArgs>? SelectionChanged;

    public AudioWaveformControl()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    /// <summary>
    /// 表示内容を一括更新する。
    /// </summary>
    public void SetContent(
        AudioWaveformAnalysisResult? analysis,
        IReadOnlyList<AudioCutRange>? cuts,
        TimeSpan? selectionStart,
        TimeSpan? selectionEnd)
    {
        _analysis = analysis;
        _cuts = cuts ?? Array.Empty<AudioCutRange>();
        _selectionStart = selectionStart;
        _selectionEnd = selectionEnd;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        drawingContext.DrawRectangle(null, PlayAreaBorderPen, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));

        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        DrawWaveforms(drawingContext);
        DrawCutRanges(drawingContext);
        DrawSelection(drawingContext);
    }

    private void DrawWaveforms(DrawingContext dc)
    {
        var channels = _analysis!.Channels;
        var trackHeight = ActualHeight / channels;
        var pen = new Pen(TrackBrush, 1d);

        for (var channel = 0; channel < channels; channel++)
        {
            var top = channel * trackHeight;
            var center = top + (trackHeight / 2d);
            dc.DrawLine(new Pen(CenterLineBrush, 1d), new Point(0, center), new Point(ActualWidth, center));

            var envelope = _analysis.Envelopes[channel];
            var bucketCount = Math.Min(envelope.Minimums.Length, envelope.Maximums.Length);
            if (bucketCount == 0) continue;

            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var x = bucketCount == 1 ? 0d : bucket * ActualWidth / (bucketCount - 1d);
                var min = Math.Clamp(envelope.Minimums[bucket], -1.25f, 1.25f);
                var max = Math.Clamp(envelope.Maximums[bucket], -1.25f, 1.25f);
                var amplitudeHeight = Math.Max(1d, (trackHeight / 2d) - 8d);
                var yTop = center - (max * amplitudeHeight);
                var yBottom = center - (min * amplitudeHeight);
                dc.DrawLine(pen, new Point(x, yTop), new Point(x, yBottom));
            }
        }
    }

    private void DrawCutRanges(DrawingContext dc)
    {
        foreach (var cut in _cuts)
        {
            var x1 = TimeToX(cut.Start);
            var x2 = TimeToX(cut.End);
            dc.DrawRectangle(CutBrush, null, new Rect(x1, 0, Math.Max(1d, x2 - x1), ActualHeight));
        }
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (!_selectionStart.HasValue || !_selectionEnd.HasValue || _selectionEnd <= _selectionStart) return;
        var x1 = TimeToX(_selectionStart.Value);
        var x2 = TimeToX(_selectionEnd.Value);
        dc.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, Math.Max(1d, x2 - x1), ActualHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero) return;
        Focus();
        CaptureMouse();
        _selecting = true;
        _selectionAnchor = XToTime(e.GetPosition(this).X);
        _selectionStart = _selectionAnchor;
        _selectionEnd = _selectionAnchor;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || e.LeftButton != MouseButtonState.Pressed) return;
        var current = XToTime(e.GetPosition(this).X);
        _selectionStart = current < _selectionAnchor ? current : _selectionAnchor;
        _selectionEnd = current < _selectionAnchor ? _selectionAnchor : current;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, new AudioWaveformSelectionChangedEventArgs(_selectionStart.Value, _selectionEnd.Value, false));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        var current = XToTime(e.GetPosition(this).X);
        _selectionStart = current < _selectionAnchor ? current : _selectionAnchor;
        _selectionEnd = current < _selectionAnchor ? _selectionAnchor : current;
        _selecting = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, new AudioWaveformSelectionChangedEventArgs(_selectionStart.Value, _selectionEnd.Value, true));
        e.Handled = true;
    }

    private double TimeToX(TimeSpan time)
    {
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero) return 0d;
        return Math.Clamp(time.TotalSeconds / _analysis.Duration.TotalSeconds, 0d, 1d) * ActualWidth;
    }

    private TimeSpan XToTime(double x)
    {
        if (_analysis is null || _analysis.Duration <= TimeSpan.Zero || ActualWidth <= 0) return TimeSpan.Zero;
        var ratio = Math.Clamp(x / ActualWidth, 0d, 1d);
        return TimeSpan.FromTicks((long)Math.Round(_analysis.Duration.Ticks * ratio));
    }
}

public sealed class AudioWaveformSelectionChangedEventArgs(TimeSpan start, TimeSpan end, bool isFinal) : EventArgs
{
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public bool IsFinal { get; } = isFinal;
}