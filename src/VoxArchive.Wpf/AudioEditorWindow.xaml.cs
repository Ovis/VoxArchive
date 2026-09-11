using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのWindow固有イベントと波形ControlをViewModelへ接続する。
/// </summary>
public partial class AudioEditorWindow : Window
{
    private readonly AudioEditorViewModel _viewModel;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _analysisStarted;

    public AudioEditorWindow(LibraryRecordingItem item)
    {
        InitializeComponent();
        _viewModel = new AudioEditorViewModel(item);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
            ModernDialog.Show(
                this,
                $"波形解析に失敗したため、この音声は編集できません。\n\n{ex.Message}",
                "音声編集",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnWaveformSelectionChanged(object? sender, AudioWaveformSelectionChangedEventArgs e)
    {
        _viewModel.SetSelection(e.Start, e.End);
        RefreshWaveform();
    }

    private void OnGainSliderMouseDown(object sender, MouseButtonEventArgs e)
        => _viewModel.BeginGainAdjustment();

    private void OnGainSliderMouseUp(object sender, MouseButtonEventArgs e)
        => _viewModel.CommitGainAdjustment();

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

    private void RefreshWaveform()
    {
        WaveformControl.SetContent(
            _viewModel.Waveform,
            _viewModel.CutRanges.Select(x => x.Range).ToArray(),
            _viewModel.SelectionStart,
            _viewModel.SelectionEnd);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.CommitGainAdjustment();
        if (!_viewModel.IsDirty) return;

        var result = ModernDialog.Show(
            this,
            "未書き出しの編集内容があります。\n編集内容を破棄して閉じますか？",
            "音声編集を閉じる",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}