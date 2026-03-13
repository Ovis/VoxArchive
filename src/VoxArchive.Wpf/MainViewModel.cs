using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Runtime;

namespace VoxArchive.Wpf;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IRecordingService _recordingService;
    private readonly ISettingsService _settingsService;
    private readonly IDeviceService _deviceService;
    private readonly IProcessCatalogService _processCatalogService;
    private RecordingOptions _options;

    private string _stateText = "状態: Stopped";
    private string _outputPathText = "出力: -";
    private string _metricsText = "統計: -";
    private string _lastErrorText = string.Empty;
    private string _selectedSpeakerDeviceId = string.Empty;
    private string _selectedMicDeviceId = string.Empty;
    private OutputCaptureMode _selectedOutputMode;
    private string _elapsedText = "00:00:00";
    private double _speakerLevelPercent;
    private double _micLevelPercent;
    private string _alignmentMillisecondsText = "0";
    private bool _isMiniMode;
    private ProcessListItem? _selectedProcessItem;

    public MainViewModel(RecordingRuntimeContext context)
    {
        _recordingService = context.RecordingService;
        _settingsService = context.SettingsService;
        _deviceService = context.DeviceService;
        _processCatalogService = context.ProcessCatalogService;
        _options = EnsureDefaults(context.DefaultOptions);

        SpeakerDevices = new ObservableCollection<AudioDeviceInfo>();
        MicDevices = new ObservableCollection<AudioDeviceInfo>();
        ProcessItems = new ObservableCollection<ProcessListItem>();
        OutputModes = new[] { OutputCaptureMode.SpeakerLoopback, OutputCaptureMode.ProcessLoopback };

        _selectedSpeakerDeviceId = _options.SpeakerDeviceId;
        _selectedMicDeviceId = _options.MicDeviceId;
        _selectedOutputMode = _options.OutputCaptureMode;
        _alignmentMillisecondsText = _options.ChannelAlignmentMilliseconds.ToString();

        StartStopCommand = new DelegateCommand(StartOrStopAsync, CanStartOrStop);
        PauseResumeCommand = new DelegateCommand(PauseOrResumeAsync, CanPauseOrResume);
        ToggleMiniModeCommand = new DelegateCommand(ToggleMiniModeAsync, () => IsStoppedOrError);
        RefreshProcessesCommand = new DelegateCommand(LoadProcessesAsync, () => IsProcessSelectionEnabled);

        _recordingService.StateChanged += (_, s) => RunOnUi(() =>
        {
            StateText = $"状態: {s}";
            OnPropertyChanged(nameof(StartStopButtonText));
            OnPropertyChanged(nameof(PauseResumeButtonText));
            OnPropertyChanged(nameof(IsDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsStoppedOrError));
            OnPropertyChanged(nameof(IsProcessSelectionEnabled));
            RefreshCommands();
        });

        _recordingService.ErrorOccurred += (_, e) => RunOnUi(() => LastErrorText = $"エラー: {e}");
        _recordingService.OutputSourceChanged += (_, e) => RunOnUi(() => LastErrorText = $"出力切替: {e.Previous} -> {e.Current} ({e.Reason})");
        _recordingService.StatisticsUpdated += (_, st) => RunOnUi(() =>
        {
            OutputPathText = $"出力: {st.OutputFilePath ?? "-"}";
            ElapsedText = st.ElapsedTime.ToString(@"hh\:mm\:ss");
            SpeakerLevelPercent = Math.Clamp(st.SpeakerLevel * 100.0, 0, 100);
            MicLevelPercent = Math.Clamp(st.MicLevel * 100.0, 0, 100);
            MetricsText = $"Drift {st.DriftCorrectionPpm:F1} ppm / MicBuf {st.MicBufferMilliseconds:F0}ms / SpkBuf {st.SpeakerBufferMilliseconds:F0}ms / UF {st.UnderflowCount} / OF {st.OverflowCount}";
        });

        _ = LoadDevicesAsync();
        _ = LoadProcessesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DelegateCommand StartStopCommand { get; }
    public DelegateCommand PauseResumeCommand { get; }
    public DelegateCommand ToggleMiniModeCommand { get; }
    public DelegateCommand RefreshProcessesCommand { get; }

    public ObservableCollection<AudioDeviceInfo> SpeakerDevices { get; }
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; }
    public ObservableCollection<ProcessListItem> ProcessItems { get; }
    public IReadOnlyList<OutputCaptureMode> OutputModes { get; }

    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }
    public string OutputPathText { get => _outputPathText; private set => SetField(ref _outputPathText, value); }
    public string MetricsText { get => _metricsText; private set => SetField(ref _metricsText, value); }
    public string LastErrorText { get => _lastErrorText; private set => SetField(ref _lastErrorText, value); }
    public string ElapsedText { get => _elapsedText; private set => SetField(ref _elapsedText, value); }
    public double SpeakerLevelPercent { get => _speakerLevelPercent; private set => SetField(ref _speakerLevelPercent, value); }
    public double MicLevelPercent { get => _micLevelPercent; private set => SetField(ref _micLevelPercent, value); }

    public string SelectedSpeakerDeviceId { get => _selectedSpeakerDeviceId; set => SetField(ref _selectedSpeakerDeviceId, value); }
    public string SelectedMicDeviceId { get => _selectedMicDeviceId; set => SetField(ref _selectedMicDeviceId, value); }
    public string AlignmentMillisecondsText { get => _alignmentMillisecondsText; set => SetField(ref _alignmentMillisecondsText, value); }

    public OutputCaptureMode SelectedOutputMode
    {
        get => _selectedOutputMode;
        set
        {
            if (SetField(ref _selectedOutputMode, value))
            {
                OnPropertyChanged(nameof(IsProcessSelectionEnabled));
                RefreshCommands();
            }
        }
    }

    public ProcessListItem? SelectedProcessItem
    {
        get => _selectedProcessItem;
        set => SetField(ref _selectedProcessItem, value);
    }

    public bool IsMiniMode
    {
        get => _isMiniMode;
        private set
        {
            if (SetField(ref _isMiniMode, value))
            {
                OnPropertyChanged(nameof(DetailsVisibility));
                OnPropertyChanged(nameof(WindowWidth));
                OnPropertyChanged(nameof(WindowHeight));
                OnPropertyChanged(nameof(MiniModeButtonText));
            }
        }
    }

    public Visibility DetailsVisibility => IsMiniMode ? Visibility.Collapsed : Visibility.Visible;
    public double WindowWidth => IsMiniMode ? 980 : 1320;
    public double WindowHeight => IsMiniMode ? 92 : 270;
    public bool IsStoppedOrError => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error;
    public bool IsDeviceSelectionEnabled => IsStoppedOrError;
    public bool IsProcessSelectionEnabled => IsDeviceSelectionEnabled && SelectedOutputMode == OutputCaptureMode.ProcessLoopback;

    public string StartStopButtonText => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error ? "録音開始" : "停止";
    public string PauseResumeButtonText => _recordingService.CurrentState == RecordingState.Paused ? "再開" : "一時停止";
    public string MiniModeButtonText => IsMiniMode ? "通常" : "ミニ";

    private async Task LoadDevicesAsync()
    {
        try
        {
            var speakers = await _deviceService.GetSpeakerDevicesAsync();
            var mics = await _deviceService.GetMicrophoneDevicesAsync();

            RunOnUi(() =>
            {
                SpeakerDevices.Clear();
                foreach (var d in speakers)
                {
                    SpeakerDevices.Add(d);
                }

                MicDevices.Clear();
                foreach (var d in mics)
                {
                    MicDevices.Add(d);
                }

                if (string.IsNullOrWhiteSpace(SelectedSpeakerDeviceId))
                {
                    SelectedSpeakerDeviceId = speakers.FirstOrDefault(x => x.IsDefault)?.DeviceId ?? speakers.FirstOrDefault()?.DeviceId ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(SelectedMicDeviceId))
                {
                    SelectedMicDeviceId = mics.FirstOrDefault(x => x.IsDefault)?.DeviceId ?? mics.FirstOrDefault()?.DeviceId ?? string.Empty;
                }
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => LastErrorText = $"デバイス列挙失敗: {ex.Message}");
        }
    }

    private async Task LoadProcessesAsync()
    {
        try
        {
            var items = await _processCatalogService.GetRunningProcessesAsync();
            RunOnUi(() =>
            {
                ProcessItems.Clear();
                foreach (var p in items)
                {
                    ProcessItems.Add(new ProcessListItem(p));
                }

                if (_options.TargetProcessId is int pid)
                {
                    SelectedProcessItem = ProcessItems.FirstOrDefault(x => x.ProcessId == pid);
                }
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => LastErrorText = $"プロセス列挙失敗: {ex.Message}");
        }
    }

    private async Task StartOrStopAsync()
    {
        if (_recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error)
        {
            LastErrorText = string.Empty;

            var mode = SelectedOutputMode;
            var targetPid = SelectedProcessItem?.ProcessId;

            if (mode == OutputCaptureMode.ProcessLoopback)
            {
                if (targetPid is null || !await _processCatalogService.ExistsAsync(targetPid.Value))
                {
                    var result = MessageBox.Show(
                        "選択したアプリは現在起動していません。\nスピーカー録音に切り替えて開始しますか？",
                        "録音開始確認",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question,
                        MessageBoxResult.OK);

                    if (result != MessageBoxResult.OK)
                    {
                        LastErrorText = "録音開始をキャンセルしました。";
                        return;
                    }

                    mode = OutputCaptureMode.SpeakerLoopback;
                    targetPid = null;
                    SelectedOutputMode = OutputCaptureMode.SpeakerLoopback;
                }
            }

            if (!int.TryParse(AlignmentMillisecondsText, out var alignmentMs))
            {
                LastErrorText = "オフセット(ms)は整数で入力してください。";
                return;
            }

            alignmentMs = Math.Clamp(alignmentMs, -500, 500);
            AlignmentMillisecondsText = alignmentMs.ToString();

            _options = EnsureDefaults(_options) with
            {
                SpeakerDeviceId = SelectedSpeakerDeviceId,
                MicDeviceId = SelectedMicDeviceId,
                OutputCaptureMode = mode,
                TargetProcessId = targetPid,
                ChannelAlignmentMilliseconds = alignmentMs
            };

            await _settingsService.SaveRecordingOptionsAsync(_options);
            var path = await _recordingService.StartAsync(_options);
            OutputPathText = $"出力: {path}";
            return;
        }

        await _recordingService.StopAsync();
    }

    private async Task PauseOrResumeAsync()
    {
        if (_recordingService.CurrentState == RecordingState.Paused)
        {
            await _recordingService.ResumeAsync();
            return;
        }

        if (_recordingService.CurrentState == RecordingState.Recording)
        {
            await _recordingService.PauseAsync();
        }
    }

    private Task ToggleMiniModeAsync()
    {
        IsMiniMode = !IsMiniMode;
        return Task.CompletedTask;
    }

    private bool CanStartOrStop()
    {
        if (_recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error)
        {
            if (SelectedOutputMode != OutputCaptureMode.ProcessLoopback)
            {
                return true;
            }

            return SelectedProcessItem is not null;
        }

        return _recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused;
    }

    private bool CanPauseOrResume()
    {
        return _recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused;
    }

    private void RefreshCommands()
    {
        StartStopCommand.RaiseCanExecuteChanged();
        PauseResumeCommand.RaiseCanExecuteChanged();
        ToggleMiniModeCommand.RaiseCanExecuteChanged();
        RefreshProcessesCommand.RaiseCanExecuteChanged();
    }

    private static RecordingOptions EnsureDefaults(RecordingOptions options)
    {
        var output = options.OutputDirectory;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoxArchive");
        }

        return options with
        {
            OutputDirectory = output
        };
    }

    private static void RunOnUi(Action action)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(action);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(SelectedOutputMode) or nameof(SelectedProcessItem))
        {
            RefreshCommands();
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProcessListItem
{
    public ProcessListItem(ProcessInfo process)
    {
        ProcessId = process.ProcessId;
        DisplayText = BuildDisplay(process);
    }

    public int ProcessId { get; }
    public string DisplayText { get; }

    private static string BuildDisplay(ProcessInfo process)
    {
        var app = string.IsNullOrWhiteSpace(process.ApplicationName) ? "(no-name)" : process.ApplicationName;
        var exe = string.IsNullOrWhiteSpace(process.ExecutableName) ? "" : $" [{process.ExecutableName}]";
        var title = string.IsNullOrWhiteSpace(process.WindowTitle) ? "" : $" - {process.WindowTitle}";
        return $"{app}{exe} (PID:{process.ProcessId}){title}";
    }
}








