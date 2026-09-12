using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 編集後ピークの事前評価、Export進捗、Gain数値入力をAudio Editorへ追加する。
/// </summary>
public partial class AudioEditorWindow
{
    private readonly AudioPeakAnalysisCache _peakAnalysisCache = new();
    private CancellationTokenSource? _peakFeedbackCancellation;
    private int _peakFeedbackRevision;
    private TextBlock? _peakFeedbackText;
    private TextBlock? _exportProgressText;
    private ProgressBar? _exportProgressBar;
    private TextBox? _channel1GainInput;
    private TextBox? _channel2GainInput;
    private bool _exportFeedbackInitialized;

    private void InitializeExportFeedback()
    {
        if (_exportFeedbackInitialized) return;
        _exportFeedbackInitialized = true;

        _exportService.PeakAnalysisCache = _peakAnalysisCache;
        AddGainNumericInputs();
        AddExportFeedbackUi();

        _viewModel.EditStateChanged += OnPeakFeedbackStateChanged;
        _viewModel.PropertyChanged += OnExportFeedbackViewModelPropertyChanged;
        ExportChannelModeComboBox.SelectionChanged += OnPeakFeedbackChannelModeChanged;
        _exportService.ProgressChanged += OnExportProgressChanged;
        Closed += OnExportFeedbackClosed;

        SyncGainNumericInputs();
        SchedulePeakFeedbackAnalysis();
    }

    private void AddGainNumericInputs()
    {
        var channelPanels = ChannelEditPanel.Children
            .OfType<StackPanel>()
            .Where(panel => Grid.GetRow(panel) is 1 or 2)
            .OrderBy(panel => Grid.GetRow(panel))
            .ToArray();

        if (channelPanels.Length >= 1)
        {
            ConfigureGainSlider(channelPanels[0], nameof(AudioEditorViewModel.Channel1GainSliderDb));
            _channel1GainInput = AddGainNumericInput(channelPanels[0], 0);
        }

        if (channelPanels.Length >= 2)
        {
            ConfigureGainSlider(channelPanels[1], nameof(AudioEditorViewModel.Channel2GainSliderDb));
            _channel2GainInput = AddGainNumericInput(channelPanels[1], 1);
        }
    }

    private static void ConfigureGainSlider(StackPanel owner, string propertyName)
    {
        var slider = owner.Children.OfType<Slider>().FirstOrDefault();
        if (slider is null) return;

        // WPF Slider自体はInfinityをValueに保持できないため、最小端だけViewModel側で-∞へ写像する。
        BindingOperations.SetBinding(slider, Slider.ValueProperty, new Binding(propertyName)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        slider.ToolTip = "最小端は-∞ dB、以降は0.5 dB単位です";
    }

    private TextBox AddGainNumericInput(StackPanel owner, int channelIndex)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0)
        };
        row.Children.Add(new TextBlock
        {
            Text = "数値 ",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
        });

        var input = new TextBox
        {
            Width = 72,
            Tag = channelIndex,
            ToolTip = "0.1 dB単位、または -∞ / -inf を入力できます"
        };
        input.KeyDown += OnGainNumericInputKeyDown;
        input.LostKeyboardFocus += OnGainNumericInputLostFocus;
        row.Children.Add(input);
        row.Children.Add(new TextBlock
        {
            Text = " dB",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
        });

        // Header / Sliderの直後へ置き、Mute/Soloの意味と混ざらないようにする。
        owner.Children.Insert(Math.Min(2, owner.Children.Count), row);
        return input;
    }

    private void AddExportFeedbackUi()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _peakFeedbackText = new TextBlock
        {
            Text = "編集後ピークを解析しています...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            FontSize = 11
        };
        panel.Children.Add(_peakFeedbackText);

        _exportProgressBar = new ProgressBar
        {
            Minimum = 0d,
            Maximum = 1d,
            Height = 7,
            Margin = new Thickness(0, 5, 0, 2),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_exportProgressBar);

        _exportProgressText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            FontSize = 11,
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_exportProgressText);
        ExportSettingsPanel.Children.Add(panel);
    }

    private void OnPeakFeedbackStateChanged(object? sender, EventArgs e)
    {
        SyncGainNumericInputs();
        SchedulePeakFeedbackAnalysis();
    }

    private void OnPeakFeedbackChannelModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SchedulePeakFeedbackAnalysis();
    }

    private void OnExportFeedbackViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioEditorViewModel.Channel1GainDb) or nameof(AudioEditorViewModel.Channel2GainDb))
        {
            SyncGainNumericInputs();
        }
        else if (e.PropertyName == nameof(AudioEditorViewModel.Waveform) && _viewModel.IsReady)
        {
            SchedulePeakFeedbackAnalysis();
        }
    }

    private void SchedulePeakFeedbackAnalysis()
    {
        _peakFeedbackCancellation?.Cancel();
        _peakFeedbackCancellation?.Dispose();
        _peakFeedbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var revision = ++_peakFeedbackRevision;
        _ = AnalyzePeakFeedbackAsync(revision, _peakFeedbackCancellation.Token);
    }

    private async Task AnalyzePeakFeedbackAsync(int revision, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            if (_viewModel.CurrentState is not { } state || !_viewModel.IsReady) return;
            if (!state.HasOutputAudio)
            {
                if (_peakFeedbackText is not null) _peakFeedbackText.Text = "編集後音声が空、または全チャンネルMuteのため書き出しできません。";
                return;
            }

            if (_peakFeedbackText is not null) _peakFeedbackText.Text = "編集後ピークを解析しています...";
            var assessment = await _peakAnalysisCache.GetOrAnalyzeAsync(
                _viewModel.SourceFilePath,
                state,
                GetSelectedChannelMode(),
                cancellationToken);
            if (revision != _peakFeedbackRevision) return;
            if (_peakFeedbackText is null) return;

            var peakText = double.IsNegativeInfinity(assessment.PeakDbfs)
                ? "-∞ dBFS"
                : $"{assessment.PeakDbfs:+0.00;-0.00;0.00} dBFS";
            if (assessment.IsClipping)
            {
                _peakFeedbackText.Text = $"Clippingあり / 実ピーク {peakText} / 自動減衰 {assessment.RequiredMasterGainDb:0.00} dB";
                _peakFeedbackText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
            else if (assessment.RequiredMasterGainDb < 0d)
            {
                _peakFeedbackText.Text = $"Clippingなし / 実ピーク {peakText} / -0.1 dBFS保護時 {assessment.RequiredMasterGainDb:0.00} dB";
                _peakFeedbackText.Foreground = System.Windows.Media.Brushes.Goldenrod;
            }
            else
            {
                _peakFeedbackText.Text = $"Clippingなし / 実ピーク {peakText} / Master減衰不要";
                _peakFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (revision == _peakFeedbackRevision && _peakFeedbackText is not null)
            {
                _peakFeedbackText.Text = $"ピーク解析失敗: {ex.Message}";
                _peakFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }
        }
    }

    private void OnExportProgressChanged(AudioEditorExportProgress e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnExportProgressChanged(e));
            return;
        }

        if (_exportProgressBar is null || _exportProgressText is null) return;
        _exportProgressBar.Visibility = Visibility.Visible;
        _exportProgressText.Visibility = Visibility.Visible;
        _exportProgressBar.Value = e.Progress;
        _exportProgressText.Text = $"{e.Message}  {e.Progress:P0}";
        if (e.Stage == AudioEditorExportStage.Completed)
        {
            _exportProgressBar.Value = 1d;
        }
    }

    private void OnGainNumericInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        ApplyGainNumericInput(input);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnGainNumericInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox input) ApplyGainNumericInput(input);
    }

    private void ApplyGainNumericInput(TextBox input)
    {
        if (!int.TryParse(input.Tag?.ToString(), out var channel)) return;
        if (!AudioGainValue.TryParse(input.Text, CultureInfo.CurrentCulture, out var value)
            && !AudioGainValue.TryParse(input.Text, CultureInfo.InvariantCulture, out value))
        {
            SyncGainNumericInputs();
            return;
        }

        _viewModel.CommitGainAdjustment();
        if (channel == 0) _viewModel.Channel1GainDb = value;
        else if (_viewModel.IsStereo) _viewModel.Channel2GainDb = value;
        SyncGainNumericInputs();
    }

    private void SyncGainNumericInputs()
    {
        if (_channel1GainInput is not null && !_channel1GainInput.IsKeyboardFocusWithin)
            _channel1GainInput.Text = AudioGainValue.Format(_viewModel.Channel1GainDb, CultureInfo.CurrentCulture);
        if (_channel2GainInput is not null && !_channel2GainInput.IsKeyboardFocusWithin)
            _channel2GainInput.Text = AudioGainValue.Format(_viewModel.Channel2GainDb, CultureInfo.CurrentCulture);
    }

    private void OnExportFeedbackClosed(object? sender, EventArgs e)
    {
        _peakFeedbackCancellation?.Cancel();
        _peakFeedbackCancellation?.Dispose();
        _peakFeedbackCancellation = null;
        _peakAnalysisCache.Clear();
        _viewModel.EditStateChanged -= OnPeakFeedbackStateChanged;
        _viewModel.PropertyChanged -= OnExportFeedbackViewModelPropertyChanged;
        ExportChannelModeComboBox.SelectionChanged -= OnPeakFeedbackChannelModeChanged;
        _exportService.ProgressChanged -= OnExportProgressChanged;
    }
}
