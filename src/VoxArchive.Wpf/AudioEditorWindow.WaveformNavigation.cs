using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public partial class AudioEditorWindow
{
    private static readonly TimeSpan InitialWaveformViewportDuration = TimeSpan.FromSeconds(60);

    private AudioWaveformDetailService? _waveformDetailService;
    private AudioWaveformDetailResult? _waveformDetail;
    private AudioWaveformViewport _waveformViewport;
    private bool _waveformViewportInitialized;
    private bool _followPlayhead = true;
    private int _detailRequestRevision;

    private void OnWaveformNavigationLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += OnWaveformNavigationPropertyChanged;
        _playbackTimer.Tick += OnWaveformFollowTick;
        Closed += OnWaveformNavigationClosed;
        InitializeAuditionUi();
        if (_viewModel.Waveform is not null) InitializeWaveformNavigation();
    }

    private void OnWaveformNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioEditorViewModel.Waveform) && _viewModel.Waveform is not null)
        {
            InitializeWaveformNavigation();
            return;
        }

        if (e.PropertyName is nameof(AudioEditorViewModel.SelectionStart) or nameof(AudioEditorViewModel.SelectionEnd))
        {
            if (!SelectionStartInput.IsKeyboardFocusWithin)
                SelectionStartInput.Text = _viewModel.SelectionStart.HasValue ? AudioEditorViewModel.FormatTime(_viewModel.SelectionStart.Value) : "00:00:00.000";
            if (!SelectionEndInput.IsKeyboardFocusWithin)
                SelectionEndInput.Text = _viewModel.SelectionEnd.HasValue ? AudioEditorViewModel.FormatTime(_viewModel.SelectionEnd.Value) : "00:00:00.000";
        }

        if (e.PropertyName is nameof(AudioEditorViewModel.SelectedCutRange)
            or nameof(AudioEditorViewModel.SelectedCutStartText)
            or nameof(AudioEditorViewModel.SelectedCutEndText))
        {
            SyncSelectedCutInputs();
            ApplyWaveformViewportState();
        }
    }

    private void InitializeWaveformNavigation()
    {
        var waveform = _viewModel.Waveform;
        if (waveform is null) return;
        _waveformDetailService?.Dispose();
        _waveformDetailService = new AudioWaveformDetailService(_viewModel.SourceFilePath);

        // 長い録音を全体表示すると編集対象が圧縮されすぎるため、最初の1分を編集開始時の作業領域とする。
        // 短い録音は従来どおり全体を表示し、「全体」操作ではいつでも全Durationへ戻せる。
        var initialEnd = waveform.Duration <= InitialWaveformViewportDuration
            ? waveform.Duration
            : InitialWaveformViewportDuration;
        _waveformViewport = AudioWaveformViewport.Normalize(TimeSpan.Zero, initialEnd, waveform.Duration);
        _waveformViewportInitialized = true;
        _waveformDetail = null;
        _followPlayhead = true;
        FollowPlayheadCheckBox.IsChecked = true;
        ApplyWaveformViewportState();
        RequestWaveformDetailAsync();
    }

    private void OnWaveformViewportRequested(object? sender, AudioWaveformViewportRequestedEventArgs e)
        => SetWaveformViewport(e.Viewport, e.Manual);

    private void OnWaveformZoomInClick(object sender, RoutedEventArgs e) => ZoomWaveform(1.8d);
    private void OnWaveformZoomOutClick(object sender, RoutedEventArgs e) => ZoomWaveform(1d / 1.8d);

    private void ZoomWaveform(double factor)
    {
        if (!_waveformViewportInitialized || _viewModel.Waveform is null) return;
        var anchor = _playhead >= _waveformViewport.Start && _playhead <= _waveformViewport.End
            ? _playhead
            : _waveformViewport.Start + TimeSpan.FromTicks(_waveformViewport.Duration.Ticks / 2);
        SetWaveformViewport(
            _waveformViewport.ZoomAround(anchor, factor, _viewModel.SourceDuration, TimeSpan.FromMilliseconds(50)),
            manual: true);
    }

    private void OnWaveformShowAllClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Waveform is null) return;
        SetWaveformViewport(AudioWaveformViewport.Full(_viewModel.SourceDuration), manual: true);
    }

    private void OnWaveformZoomSelectionClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSelection || !_viewModel.SelectionStart.HasValue || !_viewModel.SelectionEnd.HasValue) return;
        var duration = _viewModel.SelectionEnd.Value - _viewModel.SelectionStart.Value;
        var padding = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(25).Ticks, duration.Ticks / 12));
        SetWaveformViewport(
            AudioWaveformViewport.Normalize(_viewModel.SelectionStart.Value - padding, _viewModel.SelectionEnd.Value + padding, _viewModel.SourceDuration),
            manual: true);
    }

    private void OnWaveformZoomPlayheadClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SourceDuration <= TimeSpan.Zero) return;
        var desired = TimeSpan.FromSeconds(Math.Min(20d, Math.Max(2d, _viewModel.SourceDuration.TotalSeconds / 10d)));
        var half = TimeSpan.FromTicks(desired.Ticks / 2);
        SetWaveformViewport(AudioWaveformViewport.Normalize(_playhead - half, _playhead + half, _viewModel.SourceDuration), manual: true);
    }

    private void OnWaveformScrollLeftClick(object sender, RoutedEventArgs e)
        => ScrollWaveform(-0.45d);

    private void OnWaveformScrollRightClick(object sender, RoutedEventArgs e)
        => ScrollWaveform(0.45d);

    private void ScrollWaveform(double pageFraction)
    {
        if (!_waveformViewportInitialized) return;
        var delta = TimeSpan.FromTicks((long)Math.Round(_waveformViewport.Duration.Ticks * pageFraction));
        SetWaveformViewport(_waveformViewport.ScrollBy(delta, _viewModel.SourceDuration), manual: true);
    }

    private void OnWaveformGoToPlayheadClick(object sender, RoutedEventArgs e)
    {
        if (!_waveformViewportInitialized) return;
        SetWaveformViewport(_waveformViewport.EnsureVisible(_playhead, _viewModel.SourceDuration), manual: false);
    }

    private void OnFollowPlayheadChanged(object sender, RoutedEventArgs e)
        => _followPlayhead = FollowPlayheadCheckBox.IsChecked == true;

    private void OnWaveformFollowTick(object? sender, EventArgs e)
    {
        if (!_followPlayhead || !_previewService.IsPlaying || !_waveformViewportInitialized) return;
        if (_playhead >= _waveformViewport.End && _waveformViewport.End < _viewModel.SourceDuration)
        {
            SetWaveformViewport(_waveformViewport.PageForward(_viewModel.SourceDuration), manual: false);
        }
    }

    private void SetWaveformViewport(AudioWaveformViewport viewport, bool manual)
    {
        if (!_waveformViewportInitialized || _viewModel.SourceDuration <= TimeSpan.Zero) return;
        _waveformViewport = AudioWaveformViewport.Normalize(viewport.Start, viewport.End, _viewModel.SourceDuration);
        _waveformDetail = null;
        if (manual && _previewService.IsPlaying)
        {
            _followPlayhead = false;
            FollowPlayheadCheckBox.IsChecked = false;
        }
        ApplyWaveformViewportState();
        RequestWaveformDetailAsync();
    }

    private async void RequestWaveformDetailAsync()
    {
        if (_waveformDetailService is null || _viewModel.Waveform is null || !_waveformViewportInitialized) return;
        var ratio = _waveformViewport.Duration.TotalSeconds / Math.Max(0.001d, _viewModel.SourceDuration.TotalSeconds);
        if (ratio > 0.9d)
        {
            _waveformDetail = null;
            ApplyWaveformViewportState();
            return;
        }

        var revision = ++_detailRequestRevision;
        var buckets = (int)Math.Clamp(Math.Round(Math.Max(256d, WaveformControl.ActualWidth * 2.5d)), 256d, 12_000d);
        var requested = _waveformViewport;
        var result = await _waveformDetailService.GetAsync(requested, buckets, _lifetimeCancellation.Token);
        if (result is null || revision != _detailRequestRevision || requested != _waveformViewport) return;
        _waveformDetail = result;
        ApplyWaveformViewportState();
    }

    private void ApplyWaveformViewportState()
    {
        if (!_waveformViewportInitialized) return;
        WaveformControl.SetViewportState(_waveformViewport, _waveformDetail, _viewModel.SelectedCutRange?.Range);
        ViewportText.Text = $"{AudioEditorViewModel.FormatTime(_waveformViewport.Start)} - {AudioEditorViewModel.FormatTime(_waveformViewport.End)}";
    }

    private void OnWaveformCutRangeSelected(object? sender, AudioWaveformCutRangeSelectedEventArgs e)
    {
        if (_auditionPlan is not null)
        {
            _previewService.Stop();
            ClearAuditionMode();
        }
        _viewModel.SelectCutRange(e.Range);
        if (e.Range.HasValue) _viewModel.ClearSelection();
    }

    private void OnWaveformTimelineEditStarted(object? sender, EventArgs e)
    {
        CancelAuditionForTimelineEdit();
        if (_previewService.IsPlaying) _previewService.Pause();
    }

    private void OnWaveformCutBoundaryChanged(object? sender, AudioWaveformCutBoundaryChangedEventArgs e)
    {
        CutStartInput.Text = AudioEditorViewModel.FormatTime(e.Start);
        CutEndInput.Text = AudioEditorViewModel.FormatTime(e.End);
        if (!e.IsFinal) return;
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.UpdateSelectedCutRange(e.Start, e.End);
    }

    private void OnCutRangeListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_auditionPlan is not null)
        {
            _previewService.Stop();
            ClearAuditionMode();
        }
        SyncSelectedCutInputs();
        ApplyWaveformViewportState();
    }

    private void SyncSelectedCutInputs()
    {
        CutStartInput.Text = _viewModel.SelectedCutRange?.StartText ?? string.Empty;
        CutEndInput.Text = _viewModel.SelectedCutRange?.EndText ?? string.Empty;
    }

    private void OnSelectionStartFromPlayheadClick(object sender, RoutedEventArgs e)
    {
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.SetSelectionStart(_playhead);
    }

    private void OnSelectionEndFromPlayheadClick(object sender, RoutedEventArgs e)
    {
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.SetSelectionEnd(_playhead);
    }

    private void OnSelectionTimeInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        if (TryParseEditorTime(box.Text, out var value))
        {
            CancelAuditionForTimelineEdit();
            _previewService.Pause();
            if (Equals(box.Tag, "start")) _viewModel.SetSelectionStart(value);
            else _viewModel.SetSelectionEnd(value);
        }
        e.Handled = true;
    }

    private void OnCutTimeInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyCutBoundaryInputs();
        e.Handled = true;
    }

    private void OnApplyCutBoundaryClick(object sender, RoutedEventArgs e) => ApplyCutBoundaryInputs();

    private void ApplyCutBoundaryInputs()
    {
        if (_viewModel.SelectedCutRange is null) return;
        if (!TryParseEditorTime(CutStartInput.Text, out var start) || !TryParseEditorTime(CutEndInput.Text, out var end) || end <= start)
        {
            SyncSelectedCutInputs();
            return;
        }
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.UpdateSelectedCutRange(start, end);
    }

    private void OnCutHeadClick(object sender, RoutedEventArgs e)
    {
        if (_playhead <= TimeSpan.Zero) return;
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.ClearSelection();
        _viewModel.CutFromStartTo(_playhead);
    }

    private void OnCutTailClick(object sender, RoutedEventArgs e)
    {
        if (_playhead >= _viewModel.SourceDuration) return;
        CancelAuditionForTimelineEdit();
        _previewService.Pause();
        _viewModel.ClearSelection();
        _viewModel.CutFromPositionToEnd(_playhead);
    }

    private void OnWaveformNavigationClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnWaveformNavigationPropertyChanged;
        _playbackTimer.Tick -= OnWaveformFollowTick;
        _waveformDetailService?.Dispose();
        _waveformDetailService = null;
    }
}