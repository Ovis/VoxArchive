using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editor調査中だけ有効にする詳細診断ログ。
/// </summary>
/// <remarks>
/// 起動だけでなくCut、Selection、Seek、Zoom、Preview、Gain/Mute/Solo、ピーク解析、Exportまで
/// Editorセッション全体の状態遷移を記録する一時コード。原因特定後、このファイル自体を削除する。
/// </remarks>
public partial class AudioEditorWindow
{
    private bool _diagnosticsAttached;
    private DateTime _lastPlaybackDiagnosticUtc = DateTime.MinValue;

    static AudioEditorWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(AudioEditorWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDiagnosticLoaded));
        EventManager.RegisterClassHandler(
            typeof(AudioEditorWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnDiagnosticUnloaded));
    }

    private static void OnDiagnosticLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AudioEditorWindow window) return;
        App.WriteAudioEditorDiagnostic(
            $"AudioEditorWindow Loaded routed event reached. Source={SafeSourcePath(window)}, IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}");
        window.AttachDetailedDiagnostics();
        window.WriteDiagnosticState("Loaded");
    }

    private static void OnDiagnosticUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AudioEditorWindow window) return;
        window.WriteDiagnosticState("Unloaded");
        App.WriteAudioEditorDiagnostic(
            $"AudioEditorWindow Unloaded routed event reached. Source={SafeSourcePath(window)}, IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}");
    }

    private void AttachDetailedDiagnostics()
    {
        if (_diagnosticsAttached) return;
        _diagnosticsAttached = true;

        App.WriteAudioEditorDiagnostic("Attaching full Audio Editor diagnostic observers.");
        _viewModel.PropertyChanged += OnDiagnosticViewModelPropertyChanged;
        _viewModel.EditStateChanged += OnDiagnosticEditStateChanged;
        _viewModel.CutRanges.CollectionChanged += OnDiagnosticCutRangesChanged;
        _exportService.ProgressChanged += OnDiagnosticExportProgressChanged;
        _exportCoordinator.ExportStateChanged += OnDiagnosticExportStateChanged;
        _playbackTimer.Tick += OnDiagnosticPlaybackTick;
        Closed += OnDiagnosticClosed;

        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnDiagnosticButtonClick), handledEventsToo: true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnDiagnosticSelectionChanged), handledEventsToo: true);
        PreviewKeyDown += OnDiagnosticPreviewKeyDown;
        PreviewMouseDown += OnDiagnosticPreviewMouseDown;
    }

    private void OnDiagnosticClosed(object? sender, EventArgs e)
    {
        WriteDiagnosticState("Closed");
        _viewModel.PropertyChanged -= OnDiagnosticViewModelPropertyChanged;
        _viewModel.EditStateChanged -= OnDiagnosticEditStateChanged;
        _viewModel.CutRanges.CollectionChanged -= OnDiagnosticCutRangesChanged;
        _exportService.ProgressChanged -= OnDiagnosticExportProgressChanged;
        _exportCoordinator.ExportStateChanged -= OnDiagnosticExportStateChanged;
        _playbackTimer.Tick -= OnDiagnosticPlaybackTick;
        PreviewKeyDown -= OnDiagnosticPreviewKeyDown;
        PreviewMouseDown -= OnDiagnosticPreviewMouseDown;
    }

    private void OnDiagnosticViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Playhead表示など高頻度Propertyは別の間引きログへ任せる。
        if (e.PropertyName is null) return;
        if (e.PropertyName is nameof(AudioEditorViewModel.StatusText)
            or nameof(AudioEditorViewModel.Waveform)
            or nameof(AudioEditorViewModel.IsAnalyzing)
            or nameof(AudioEditorViewModel.AnalysisProgress)
            or nameof(AudioEditorViewModel.SelectionStart)
            or nameof(AudioEditorViewModel.SelectionEnd)
            or nameof(AudioEditorViewModel.SelectedCutRange)
            or nameof(AudioEditorViewModel.Channel1GainDb)
            or nameof(AudioEditorViewModel.Channel2GainDb)
            or nameof(AudioEditorViewModel.Channel1Muted)
            or nameof(AudioEditorViewModel.Channel2Muted)
            or nameof(AudioEditorViewModel.IsDirty))
        {
            WriteDiagnosticState($"ViewModel.PropertyChanged:{e.PropertyName}");
        }
    }

    private void OnDiagnosticEditStateChanged(object? sender, EventArgs e)
        => WriteDiagnosticState("EditStateChanged");

    private void OnDiagnosticCutRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => WriteDiagnosticState($"CutRanges.CollectionChanged:{e.Action}");

    private void OnDiagnosticExportProgressChanged(AudioEditorExportProgress progress)
    {
        App.WriteAudioEditorDiagnostic(
            $"Export progress. Stage={progress.Stage}, Progress={progress.Progress:F4}, Message={progress.Message}");
        WriteDiagnosticState($"ExportProgress:{progress.Stage}");
    }

    private void OnDiagnosticExportStateChanged(object? sender, EventArgs e)
        => WriteDiagnosticState($"ExportCoordinatorChanged:IsExporting={_exportCoordinator.IsExporting}");

    private void OnDiagnosticPlaybackTick(object? sender, EventArgs e)
    {
        if (!_previewService.IsLoaded) return;
        var now = DateTime.UtcNow;
        if (now - _lastPlaybackDiagnosticUtc < TimeSpan.FromSeconds(2)) return;
        _lastPlaybackDiagnosticUtc = now;
        WriteDiagnosticState("PlaybackHeartbeat");
    }

    private void OnDiagnosticButtonClick(object sender, RoutedEventArgs e)
    {
        var source = e.OriginalSource as FrameworkElement;
        var button = FindDiagnosticParent<ButtonBase>(source);
        var content = button switch
        {
            Button b => b.Content?.ToString(),
            CheckBox c => c.Content?.ToString(),
            _ => null
        };
        App.WriteAudioEditorDiagnostic(
            $"UI Click. Control={button?.GetType().Name ?? source?.GetType().Name ?? "unknown"}, Name={button?.Name ?? source?.Name ?? string.Empty}, Content={content ?? string.Empty}");
        WriteDiagnosticState("UI.Click");
    }

    private void OnDiagnosticSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source) return;
        App.WriteAudioEditorDiagnostic(
            $"UI SelectionChanged. Control={source.GetType().Name}, Name={source.Name}, Added={e.AddedItems.Count}, Removed={e.RemovedItems.Count}");
        WriteDiagnosticState("UI.SelectionChanged");
    }

    private void OnDiagnosticPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        var source = e.OriginalSource as FrameworkElement;
        App.WriteAudioEditorDiagnostic(
            $"UI KeyDown. Key={e.Key}, Modifiers={Keyboard.Modifiers}, Control={source?.GetType().Name ?? "unknown"}, Name={source?.Name ?? string.Empty}");
    }

    private void OnDiagnosticPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 波形上のSelection/Seek/Cut境界操作を追跡する。座標だけを記録し、音声内容は記録しない。
        var position = e.GetPosition(this);
        var source = e.OriginalSource as FrameworkElement;
        App.WriteAudioEditorDiagnostic(
            $"UI MouseDown. Button={e.ChangedButton}, ClickCount={e.ClickCount}, X={position.X:F1}, Y={position.Y:F1}, Control={source?.GetType().Name ?? "unknown"}, Name={source?.Name ?? string.Empty}");
    }

    /// <summary>
    /// Audio Editorの重要状態を1行にまとめて同期診断ログへ出力する。
    /// </summary>
    private void WriteDiagnosticState(string reason)
    {
        try
        {
            var state = _viewModel.CurrentState;
            var builder = new StringBuilder();
            builder.Append("EditorState. Reason=").Append(reason);
            builder.Append(", Source=").Append(_viewModel.SourceFilePath);
            builder.Append(", Ready=").Append(_viewModel.IsReady);
            builder.Append(", Analyzing=").Append(_viewModel.IsAnalyzing);
            builder.Append(", AnalysisProgress=").Append(_viewModel.AnalysisProgress.ToString("F4"));
            builder.Append(", Dirty=").Append(_viewModel.IsDirty);
            builder.Append(", Status=").Append(_viewModel.StatusText.Replace(Environment.NewLine, " "));
            builder.Append(", SourceDuration=").Append(_viewModel.SourceDuration.TotalMilliseconds.ToString("F0"));
            builder.Append(", EditedDuration=").Append(_viewModel.EditedDuration.TotalMilliseconds.ToString("F0"));
            builder.Append(", PlayheadMs=").Append(_playhead.TotalMilliseconds.ToString("F0"));
            builder.Append(", PreviewLoaded=").Append(_previewService.IsLoaded);
            builder.Append(", PreviewPlaying=").Append(_previewService.IsPlaying);
            builder.Append(", PreviewEdited=").Append(_previewService.IsEditedMode);
            builder.Append(", PreviewPosMs=").Append(_previewService.Position.TotalMilliseconds.ToString("F0"));
            builder.Append(", Solo=").Append(_soloChannel?.ToString() ?? "none");
            builder.Append(", ExportingLocal=").Append(_isExporting);
            builder.Append(", ExportingGlobal=").Append(_exportCoordinator.IsExporting);
            builder.Append(", Selection=")
                .Append(_viewModel.SelectionStart?.TotalMilliseconds.ToString("F0") ?? "null")
                .Append("-")
                .Append(_viewModel.SelectionEnd?.TotalMilliseconds.ToString("F0") ?? "null");
            builder.Append(", SelectedCut=").Append(_viewModel.SelectedCutRange?.DisplayText ?? "none");
            builder.Append(", Cuts=[").Append(string.Join(";", _viewModel.CutRanges.Select(x => x.DisplayText))).Append(']');

            if (state is not null)
            {
                builder.Append(", HasOutputAudio=").Append(state.HasOutputAudio);
                builder.Append(", ChannelCount=").Append(state.ChannelCount);
                for (var i = 0; i < state.Channels.Count; i++)
                {
                    var channel = state.Channels[i];
                    builder.Append(", CH").Append(i + 1)
                        .Append("Gain=").Append(double.IsNegativeInfinity(channel.GainDb) ? "-inf" : channel.GainDb.ToString("F1"))
                        .Append(", CH").Append(i + 1).Append("Mute=").Append(channel.IsMuted);
                }
            }

            if (_waveformViewportInitialized)
            {
                builder.Append(", ViewportMs=")
                    .Append(_waveformViewport.Start.TotalMilliseconds.ToString("F0"))
                    .Append('-')
                    .Append(_waveformViewport.End.TotalMilliseconds.ToString("F0"));
                builder.Append(", FollowPlayhead=").Append(_followPlayhead);
                builder.Append(", DetailLoaded=").Append(_waveformDetail is not null);
            }

            App.WriteAudioEditorDiagnostic(builder.ToString());
        }
        catch (Exception ex)
        {
            App.WriteAudioEditorDiagnostic($"Failed to build Audio Editor state diagnostic. Reason={reason}, Exception={ex}");
        }
    }

    private static T? FindDiagnosticParent<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T matched) return matched;
            try
            {
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static string SafeSourcePath(AudioEditorWindow window)
    {
        try
        {
            return window.SourceFilePath;
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }
}
