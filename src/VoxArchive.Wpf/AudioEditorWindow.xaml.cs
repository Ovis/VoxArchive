using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのWindow固有イベントと波形Control、Preview、ExportをViewModelへ接続する。
/// </summary>
public partial class AudioEditorWindow : Window
{
    private static readonly TimeSpan PlaybackPositionInterval = TimeSpan.FromMilliseconds(100);

    private readonly AudioEditorViewModel _viewModel;
    private readonly AudioEditorPreviewService _previewService;
    private readonly AudioEditorExportService _exportService;
    private readonly AudioExportCoordinator _exportCoordinator;
    private readonly AudioSourceFileGuard _sourceGuard;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _exportCancellation;
    private bool _analysisStarted;
    private bool _isExporting;
    private bool _closeAfterExportCancellation;
    private bool _suppressSoloEvents;
    private string? _lastExportDirectory;
    private TimeSpan _playhead;
    private int? _soloChannel;

    public AudioEditorWindow(
        LibraryRecordingItem item,
        IRecordingPlaybackService playbackService,
        AudioExportCoordinator exportCoordinator,
        RecordingCatalogService catalogService)
    {
        InitializeComponent();
        _viewModel = new AudioEditorViewModel(item);
        _previewService = new AudioEditorPreviewService(playbackService);
        _exportCoordinator = exportCoordinator ?? throw new ArgumentNullException(nameof(exportCoordinator));
        _exportService = new AudioEditorExportService(exportCoordinator, catalogService);
        _sourceGuard = new AudioSourceFileGuard(item.FilePath);
        DataContext = _viewModel;

        // 再生位置更新は入力より低い優先度で処理する。
        // 波形全体の再描画がMouse/Keyboard入力を飢餓状態にしないことを優先する。
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PlaybackPositionInterval };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.EditStateChanged += OnEditStateChanged;
        _viewModel.CutRanges.CollectionChanged += OnCutRangesChanged;
        _exportCoordinator.ExportStateChanged += OnExportStateChanged;
    }

    public string SourceFilePath => _viewModel.SourceFilePath;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_analysisStarted) return;
        _analysisStarted = true;
        _playbackTimer.Start();
        UpdateExportLocks();

        try
        {
            _sourceGuard.ValidateUnchanged();
            var progress = new Progress<double>(_viewModel.ReportAnalysisProgress);
            var result = await AudioWaveformAnalysisService.AnalyzeAsync(
                _viewModel.SourceFilePath,
                progress,
                _lifetimeCancellation.Token);
            _sourceGuard.ValidateUnchanged();
            _viewModel.CompleteAnalysis(result);
            ExportChannelModeComboBox.IsEnabled = _viewModel.IsStereo;
            if (!_viewModel.IsStereo)
            {
                ExportChannelModeComboBox.SelectedIndex = 0;
                SoloChannel1CheckBox.Visibility = Visibility.Collapsed;
                SoloChannel2CheckBox.Visibility = Visibility.Collapsed;
            }
            SetPlayhead(TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _viewModel.FailAnalysis(ex);
            ModernDialog.Show(this, $"波形解析に失敗したため、この音声は編集できません。\n\n{ex.Message}", "音声編集", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnWaveformSelectionChanged(object? sender, AudioWaveformSelectionChangedEventArgs e)
    {
        if (!e.IsFinal && _previewService.IsPlaying) _previewService.Pause();
        _viewModel.SetSelection(e.Start, e.End);
        RefreshWaveform();
    }

    private void OnWaveformSeekRequested(object? sender, AudioWaveformSeekRequestedEventArgs e)
    {
        _viewModel.ClearSelection();
        SeekToSourcePosition(e.Position, AudioSeekDirection.Forward);
    }

    private void OnGainSliderMouseDown(object sender, MouseButtonEventArgs e) => _viewModel.BeginGainAdjustment();
    private void OnGainSliderMouseUp(object sender, MouseButtonEventArgs e) => _viewModel.CommitGainAdjustment();

    private async void OnPreviewPlayClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentState is not { } state) return;
        _viewModel.CommitGainAdjustment();
        state = _viewModel.CurrentState!;

        try
        {
            _sourceGuard.ValidateUnchanged();
            var sourcePosition = GetResolvedPlayheadForMode(_playhead, AudioSeekDirection.Forward);
            var speed = GetSelectedPlaybackSpeed();
            if (PreviewModeComboBox.SelectedIndex == 1)
            {
                await _previewService.PlayOriginalAsync(_viewModel.SourceFilePath, state, _soloChannel, speed, _lifetimeCancellation.Token);
                _previewService.Seek(sourcePosition);
                _viewModel.StatusText = $"元音声を {speed:0.0}x で再生しています。";
            }
            else
            {
                _viewModel.StatusText = "編集後プレビューを準備しています...";
                await _previewService.PlayEditedAsync(
                    _viewModel.SourceFilePath,
                    state,
                    GetSelectedChannelMode(),
                    _soloChannel,
                    speed,
                    _lifetimeCancellation.Token);
                var mapper = GetTimelineMapper();
                _previewService.Seek(mapper.SourceToRendered(sourcePosition));
                _viewModel.StatusText = $"編集後音声を {speed:0.0}x で再生しています。";
            }
            SetPlayhead(sourcePosition);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"プレビュー再生に失敗しました: {ex.Message}";
        }
    }

    private void OnPreviewPauseClick(object sender, RoutedEventArgs e)
    {
        App.WriteAudioEditorDiagnostic($"Preview Pause handler entered. Playing={_previewService.IsPlaying}, PositionMs={_previewService.Position.TotalMilliseconds:F0}");
        _previewService.Pause();
        UpdatePlayheadFromPlayback();
        _viewModel.StatusText = "プレビューを一時停止しました。";
        App.WriteAudioEditorDiagnostic($"Preview Pause handler completed. Playing={_previewService.IsPlaying}, PositionMs={_previewService.Position.TotalMilliseconds:F0}");
    }

    private void OnPreviewStopClick(object sender, RoutedEventArgs e)
    {
        App.WriteAudioEditorDiagnostic($"Preview Stop handler entered. Playing={_previewService.IsPlaying}, PositionMs={_previewService.Position.TotalMilliseconds:F0}");
        _previewService.Stop();
        SetPlayhead(TimeSpan.Zero);
        ClearAuditionMode();
        _viewModel.StatusText = "プレビューを停止しました。";
        App.WriteAudioEditorDiagnostic($"Preview Stop handler completed. Playing={_previewService.IsPlaying}, PositionMs={_previewService.Position.TotalMilliseconds:F0}");
    }

    private void OnPlaybackSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _previewService.SetPlaybackSpeed(GetSelectedPlaybackSpeed());
    }

    private async void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !_previewService.IsPlaying) return;
        await RestartPreviewAtCurrentPositionAsync();
    }

    private async void OnPreviewRenderSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _previewService.InvalidateMonitorPreview();
        if (_previewService.IsPlaying) await RestartPreviewAtCurrentPositionAsync();
    }

    private async void OnSoloChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSoloEvents || sender is not CheckBox changed) return;
        _suppressSoloEvents = true;
        try
        {
            if (changed.IsChecked == true && int.TryParse(changed.Tag?.ToString(), out var channel))
            {
                _soloChannel = channel;
                if (channel == 0) SoloChannel2CheckBox.IsChecked = false;
                else SoloChannel1CheckBox.IsChecked = false;
            }
            else if ((_soloChannel == 0 && changed == SoloChannel1CheckBox) || (_soloChannel == 1 && changed == SoloChannel2CheckBox))
            {
                _soloChannel = null;
            }
        }
        finally
        {
            _suppressSoloEvents = false;
        }

        _previewService.InvalidateMonitorPreview();
        if (_previewService.IsPlaying) await RestartPreviewAtCurrentPositionAsync();
    }

    private void OnSeekBackwardClick(object sender, RoutedEventArgs e)
        => SeekRelative(TimeSpan.FromSeconds(-5), AudioSeekDirection.Backward);

    private void OnSeekForwardClick(object sender, RoutedEventArgs e)
        => SeekRelative(TimeSpan.FromSeconds(5), AudioSeekDirection.Forward);

    private void SeekRelative(TimeSpan delta, AudioSeekDirection direction)
    {
        var target = _playhead + delta;
        SeekToSourcePosition(target, direction);
    }

    private void OnPlayheadInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (TryParseEditorTime(PlayheadInput.Text, out var target))
        {
            SeekToSourcePosition(target, AudioSeekDirection.Forward);
        }
        else
        {
            PlayheadInput.Text = AudioEditorViewModel.FormatTime(_playhead);
        }
        e.Handled = true;
    }

    private void SeekToSourcePosition(TimeSpan target, AudioSeekDirection direction)
    {
        if (_viewModel.CurrentState is null) return;
        if (_auditionPlan is not null)
        {
            ClearAuditionMode();
        }
        var resolved = GetResolvedPlayheadForMode(target, direction);
        SetPlayhead(resolved);
        if (!_previewService.IsLoaded) return;

        if (PreviewModeComboBox.SelectedIndex == 1)
        {
            _previewService.Seek(resolved);
        }
        else
        {
            _previewService.Seek(GetTimelineMapper().SourceToRendered(resolved, direction));
        }
    }

    private TimeSpan GetResolvedPlayheadForMode(TimeSpan target, AudioSeekDirection direction)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > _viewModel.SourceDuration ? _viewModel.SourceDuration : target;
        return PreviewModeComboBox.SelectedIndex == 1 ? target : GetTimelineMapper().ResolveSourceSeekTarget(target, direction);
    }

    private async Task RestartPreviewAtCurrentPositionAsync()
    {
        var sourcePosition = _playhead;
        _previewService.Pause();
        OnPreviewPlayClick(this, new RoutedEventArgs());
        await Task.Yield();
        SetPlayhead(sourcePosition);
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e) => UpdatePlayheadFromPlayback();

    private void UpdatePlayheadFromPlayback()
    {
        if (!_previewService.IsLoaded || _viewModel.CurrentState is null) return;
        var sourcePosition = _previewService.IsEditedMode
            ? GetTimelineMapper().RenderedToSource(_previewService.Position)
            : _previewService.Position;
        SetPlayhead(sourcePosition);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_isExporting || _viewModel.CurrentState is not { } state) return;
        _viewModel.CommitGainAdjustment();
        state = _viewModel.CurrentState!;

        if (!state.HasOutputAudio)
        {
            ModernDialog.Show(this, "編集後音声が空、または全チャンネルが無音のため書き出しできません。", "書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var format = GetSelectedExportFormat();
        var extension = format switch
        {
            AudioEditorExportFormat.Wav => ".wav",
            AudioEditorExportFormat.Mp3 => ".mp3",
            _ => ".flac"
        };
        var sourceDirectory = Path.GetDirectoryName(_viewModel.SourceFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var initialDirectory = Directory.Exists(_lastExportDirectory) ? _lastExportDirectory! : sourceDirectory;
        var baseName = Path.GetFileNameWithoutExtension(_viewModel.SourceFilePath) + "_edited";
        var dialog = new SaveFileDialog
        {
            Title = "編集後音声を書き出し",
            InitialDirectory = initialDirectory,
            FileName = baseName + extension,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = format switch
            {
                AudioEditorExportFormat.Wav => "WAV (*.wav)|*.wav",
                AudioEditorExportFormat.Mp3 => "MP3 (*.mp3)|*.mp3",
                _ => "FLAC (*.flac)|*.flac"
            }
        };
        if (dialog.ShowDialog(this) != true) return;

        _isExporting = true;
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        UpdateExportLocks();
        _previewService.Pause();
        try
        {
            _sourceGuard.ValidateUnchanged();
            _viewModel.StatusText = "書き出し用ピーク解析とレンダリングを実行しています...";
            var app = (App)System.Windows.Application.Current;
            var holder = app.Services.GetRequiredService<RecordingRuntimeContextHolder>();
            var ffmpegPath = holder.Context?.DefaultOptions.FfmpegExecutablePath;
            var addToLibrary = format == AudioEditorExportFormat.Flac && AddToLibraryCheckBox.IsChecked == true;
            var result = await _exportService.ExportAsync(
                _viewModel.SourceFilePath,
                dialog.FileName,
                state,
                GetSelectedChannelMode(),
                format,
                AutoAttenuateCheckBox.IsChecked == true,
                addToLibrary,
                _viewModel.SourceTitle + "（編集済み）",
                ffmpegPath,
                _exportCancellation.Token);

            _lastExportDirectory = Path.GetDirectoryName(dialog.FileName);
            _viewModel.MarkExported();
            var renderResult = result.RenderResult;
            if (result.LibraryRegistrationAttempted && !result.LibraryRegistrationSucceeded)
            {
                _viewModel.StatusText = $"書き出しは完了しましたが、ライブラリ登録に失敗しました: {result.LibraryRegistrationError}";
                ModernDialog.Show(this, _viewModel.StatusText, "ライブラリ登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (renderResult.AutoAttenuated)
            {
                _viewModel.StatusText = $"書き出しました。Clipping防止のためMaster {renderResult.AppliedMasterGainDb:F2} dBを適用しました。";
            }
            else
            {
                _viewModel.StatusText = result.LibraryRegistrationSucceeded
                    ? $"書き出してライブラリへ追加しました: {dialog.FileName}"
                    : $"書き出しました: {dialog.FileName}";
            }
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "書き出しをキャンセルしました。";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"書き出しに失敗しました: {ex.Message}";
            ModernDialog.Show(this, ex.Message, "書き出しエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            _isExporting = false;
            UpdateExportLocks();
            if (_closeAfterExportCancellation)
            {
                _closeAfterExportCancellation = false;
                Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private void OnCancelExportClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void OnExportFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        AddToLibraryCheckBox.IsEnabled = GetSelectedExportFormat() == AudioEditorExportFormat.Flac && !_exportCoordinator.IsExporting;
    }

    private double GetSelectedPlaybackSpeed()
    {
        if (PlaybackSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string text } &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            return speed;
        }
        return 1d;
    }

    private AudioEditorExportFormat GetSelectedExportFormat() => ExportFormatComboBox.SelectedIndex switch
    {
        0 => AudioEditorExportFormat.Wav,
        1 => AudioEditorExportFormat.Mp3,
        _ => AudioEditorExportFormat.Flac
    };

    private AudioRenderChannelMode GetSelectedChannelMode()
        => !_viewModel.IsStereo || ExportChannelModeComboBox.SelectedIndex == 0
            ? AudioRenderChannelMode.MonoMixdown
            : AudioRenderChannelMode.Stereo;

    private AudioTimelineMapper GetTimelineMapper()
    {
        var state = _viewModel.CurrentState ?? throw new InvalidOperationException("編集状態が初期化されていません。");
        var sampleRate = _viewModel.Waveform?.SampleRate ?? throw new InvalidOperationException("波形解析が完了していません。");
        return new AudioTimelineMapper(state, sampleRate);
    }

    private void OnEditStateChanged(object? sender, EventArgs e)
    {
        if (!_previewService.IsPlaying)
        {
            _previewService.InvalidateEditedPreview();
        }
        SetPlayhead(GetResolvedPlayheadForMode(_playhead, AudioSeekDirection.Forward));
    }

    private void OnCutRangesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_previewService.IsPlaying) _previewService.Pause();
        _previewService.InvalidateEditedPreview();
        SetPlayhead(GetResolvedPlayheadForMode(_playhead, AudioSeekDirection.Forward));
    }

    private void OnExportStateChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(UpdateExportLocks);

    private void UpdateExportLocks()
    {
        if (!IsLoaded) return;
        var locked = _exportCoordinator.IsExporting;
        CutEditPanel.IsEnabled = !locked;
        ChannelEditPanel.IsEnabled = !locked;
        UndoPanel.IsEnabled = !locked;
        ExportFormatComboBox.IsEnabled = !locked;
        ExportChannelModeComboBox.IsEnabled = !locked && _viewModel.IsStereo;
        AutoAttenuateCheckBox.IsEnabled = !locked;
        AddToLibraryCheckBox.IsEnabled = !locked && GetSelectedExportFormat() == AudioEditorExportFormat.Flac;
        ExportButton.IsEnabled = !locked;
        CancelExportButton.IsEnabled = _isExporting;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            if (_viewModel.UndoCommand.CanExecute(null)) _viewModel.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            if (_viewModel.RedoCommand.CanExecute(null)) _viewModel.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Left)
        {
            SeekRelative(TimeSpan.FromSeconds(-5), AudioSeekDirection.Backward);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Right)
        {
            SeekRelative(TimeSpan.FromSeconds(5), AudioSeekDirection.Forward);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && _viewModel.IsReady)
        {
            App.WriteAudioEditorDiagnostic($"Space toggle entered. Playing={_previewService.IsPlaying}, PositionMs={_previewService.Position.TotalMilliseconds:F0}");
            if (_previewService.IsPlaying)
            {
                OnPreviewPauseClick(this, new RoutedEventArgs());
            }
            else
            {
                OnPreviewPlayClick(this, new RoutedEventArgs());
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _viewModel.ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _viewModel.RemoveSelectedCutCommand.CanExecute(null))
        {
            _viewModel.RemoveSelectedCutCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioEditorViewModel.Waveform)
            or nameof(AudioEditorViewModel.SelectionStart)
            or nameof(AudioEditorViewModel.SelectionEnd)
            or nameof(AudioEditorViewModel.EditedDuration))
        {
            RefreshWaveform();
        }
    }

    private void SetPlayhead(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value > _viewModel.SourceDuration ? _viewModel.SourceDuration : value;
        _playhead = value;
        var text = AudioEditorViewModel.FormatTime(value);
        PlayheadText.Text = text;
        if (!PlayheadInput.IsKeyboardFocusWithin) PlayheadInput.Text = text;
        WaveformControl.SetPlayhead(value);
    }

    private void RefreshWaveform()
    {
        WaveformControl.SetContent(
            _viewModel.Waveform,
            _viewModel.CutRanges.Select(x => x.Range).ToArray(),
            _viewModel.SelectionStart,
            _viewModel.SelectionEnd,
            _playhead);
    }

    private static bool TryParseEditorTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mainParts = text.Trim().Split(':');
        if (mainParts.Length != 3 || !int.TryParse(mainParts[0], out var hours) || !int.TryParse(mainParts[1], out var minutes)) return false;
        var secondParts = mainParts[2].Split('.');
        if (secondParts.Length is < 1 or > 2 || !int.TryParse(secondParts[0], out var seconds)) return false;
        var millisecondsText = secondParts.Length == 2 ? secondParts[1].PadRight(3, '0') : "000";
        if (millisecondsText.Length > 3) millisecondsText = millisecondsText[..3];
        if (!int.TryParse(millisecondsText, out var milliseconds)) return false;
        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59 || milliseconds is < 0 or > 999) return false;
        value = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExporting)
        {
            var result = ModernDialog.Show(this, "書き出しをキャンセルして閉じますか？", "音声編集を閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            _closeAfterExportCancellation = true;
            _exportCancellation?.Cancel();
            return;
        }

        _viewModel.CommitGainAdjustment();
        if (!_viewModel.IsDirty) return;
        var dirtyResult = ModernDialog.Show(this, "未書き出しの編集内容があります。\n編集内容を破棄して閉じますか？", "音声編集を閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (dirtyResult != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _playbackTimer.Stop();
        _lifetimeCancellation.Cancel();
        _previewService.Dispose();
        _exportCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.EditStateChanged -= OnEditStateChanged;
        _viewModel.CutRanges.CollectionChanged -= OnCutRangesChanged;
        _exportCoordinator.ExportStateChanged -= OnExportStateChanged;
    }
}
