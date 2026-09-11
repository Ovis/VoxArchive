using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのWindow固有イベントと波形Control、Preview、ExportをViewModelへ接続する。
/// </summary>
public partial class AudioEditorWindow : Window
{
    private readonly AudioEditorViewModel _viewModel;
    private readonly AudioEditorPreviewService _previewService;
    private readonly AudioEditorExportService _exportService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _analysisStarted;
    private bool _isExporting;
    private string? _lastExportDirectory;

    public AudioEditorWindow(
        LibraryRecordingItem item,
        IRecordingPlaybackService playbackService,
        AudioExportCoordinator exportCoordinator)
    {
        InitializeComponent();
        _viewModel = new AudioEditorViewModel(item);
        _previewService = new AudioEditorPreviewService(playbackService);
        _exportService = new AudioEditorExportService(exportCoordinator);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.EditStateChanged += OnEditStateChanged;
        _viewModel.CutRanges.CollectionChanged += (_, _) => RefreshWaveform();
    }

    public string SourceFilePath => _viewModel.SourceFilePath;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_analysisStarted) return;
        _analysisStarted = true;

        try
        {
            var progress = new Progress<double>(_viewModel.ReportAnalysisProgress);
            var result = await AudioWaveformAnalysisService.AnalyzeAsync(
                _viewModel.SourceFilePath,
                progress,
                _lifetimeCancellation.Token);
            _viewModel.CompleteAnalysis(result);
            RefreshWaveform();
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
        _viewModel.SetSelection(e.Start, e.End);
        RefreshWaveform();
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
            var speed = GetSelectedPlaybackSpeed();
            if (PreviewModeComboBox.SelectedIndex == 1)
            {
                _previewService.PlayOriginal(_viewModel.SourceFilePath, speed);
                _viewModel.StatusText = $"元音声を {speed:0.0}x で再生しています。";
            }
            else
            {
                _viewModel.StatusText = "編集後プレビューを準備しています...";
                await _previewService.PlayEditedAsync(_viewModel.SourceFilePath, state, speed, _lifetimeCancellation.Token);
                _viewModel.StatusText = $"編集後音声を {speed:0.0}x で再生しています。";
            }
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
        _previewService.Pause();
        _viewModel.StatusText = "プレビューを一時停止しました。";
    }

    private void OnPreviewStopClick(object sender, RoutedEventArgs e)
    {
        _previewService.Stop();
        _viewModel.StatusText = "プレビューを停止しました。";
    }

    private void OnPlaybackSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _previewService.SetPlaybackSpeed(GetSelectedPlaybackSpeed());
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

        var channelMode = !_viewModel.IsStereo || ExportChannelModeComboBox.SelectedIndex == 0
            ? AudioRenderChannelMode.MonoMixdown
            : AudioRenderChannelMode.Stereo;

        _isExporting = true;
        OperationPanel.IsEnabled = false;
        _previewService.Pause();
        try
        {
            _viewModel.StatusText = "書き出し用ピーク解析とレンダリングを実行しています...";
            var app = (App)Application.Current;
            var holder = app.Services.GetRequiredService<RecordingRuntimeContextHolder>();
            var ffmpegPath = holder.Context?.DefaultOptions.FfmpegExecutablePath;
            var result = await _exportService.ExportAsync(
                _viewModel.SourceFilePath,
                dialog.FileName,
                state,
                channelMode,
                format,
                ffmpegPath,
                _lifetimeCancellation.Token);
            _lastExportDirectory = Path.GetDirectoryName(dialog.FileName);
            _viewModel.MarkExported();
            _viewModel.StatusText = result.AutoAttenuated
                ? $"書き出しました。Clipping防止のためMaster {result.AppliedMasterGainDb:F2} dBを適用しました。"
                : $"書き出しました: {dialog.FileName}";
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
            _isExporting = false;
            OperationPanel.IsEnabled = true;
        }
    }

    private double GetSelectedPlaybackSpeed()
    {
        if (PlaybackSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string text } &&
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed))
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

    private void OnEditStateChanged(object? sender, EventArgs e)
    {
        // 再生中のGain/Mute操作で勝手に停止させない。次回再生時には必ず最新状態を再レンダリングする。
        if (!_previewService.IsPlaying)
        {
            _previewService.InvalidateEditedPreview();
        }
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
        if (e.Key == Key.Space && _viewModel.IsReady)
        {
            OnPreviewPlayClick(this, new RoutedEventArgs());
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
        if (e.PropertyName is nameof(AudioEditorViewModel.Waveform) or nameof(AudioEditorViewModel.SelectionStart) or nameof(AudioEditorViewModel.SelectionEnd) or nameof(AudioEditorViewModel.EditedDuration))
        {
            RefreshWaveform();
        }
    }

    private void RefreshWaveform()
    {
        WaveformControl.SetContent(_viewModel.Waveform, _viewModel.CutRanges.Select(x => x.Range).ToArray(), _viewModel.SelectionStart, _viewModel.SelectionEnd);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExporting)
        {
            var result = ModernDialog.Show(this, "書き出し中です。キャンセルして閉じますか？", "音声編集を閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _lifetimeCancellation.Cancel();
        }

        _viewModel.CommitGainAdjustment();
        if (!_viewModel.IsDirty) return;
        var dirtyResult = ModernDialog.Show(this, "未書き出しの編集内容があります。\n編集内容を破棄して閉じますか？", "音声編集を閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (dirtyResult != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _previewService.Dispose();
        _lifetimeCancellation.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.EditStateChanged -= OnEditStateChanged;
    }
}
