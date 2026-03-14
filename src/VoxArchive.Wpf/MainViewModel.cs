using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
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
    private string _startStopHotkeyText = KeyboardShortcutHelper.DefaultStartStopHotkey;
    private bool _isMiniMode;
    private ProcessListItem? _selectedProcessItem;
    private bool _isSpeakerCaptureEnabled = true;
    private bool _isMicCaptureEnabled = true;
    private bool _isSpeakerDevicePopupOpenNormal;
    private bool _isMicDevicePopupOpenNormal;
    private bool _isSpeakerDevicePopupOpenMini;
    private bool _isMicDevicePopupOpenMini;
    private bool _isProcessPopupOpenNormal;
    private bool _isProcessPopupOpenMini;
    private bool _isRefreshingDeviceList;
    private LibraryWindow? _libraryWindow;

    private const double MeterFloorDb = -60d;
    private const double MeterCeilingDb = 0d;
    private const double MeterDisplayGainDb = 6d;
    private const string SystemDefaultDeviceId = "__system_default__";

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

        _selectedSpeakerDeviceId = _options.SpeakerDeviceId;
        _selectedMicDeviceId = _options.MicDeviceId;
        _selectedOutputMode = _options.OutputCaptureMode;
        _alignmentMillisecondsText = _options.ChannelAlignmentMilliseconds.ToString();
        _startStopHotkeyText = _options.StartStopHotkey;
        _isSpeakerCaptureEnabled = _recordingService.IsSpeakerCaptureEnabled;
        _isMicCaptureEnabled = _recordingService.IsMicCaptureEnabled;

        StartStopCommand = new DelegateCommand(StartOrStopAsync, CanStartOrStop);
        PauseResumeCommand = new DelegateCommand(PauseOrResumeAsync, CanPauseOrResume);
        ToggleMiniModeCommand = new DelegateCommand(ToggleMiniModeAsync, () => IsStoppedOrError);
        RefreshProcessesCommand = new DelegateCommand(LoadProcessesAsync, () => IsProcessSelectionEnabled);
        ToggleSpeakerCaptureCommand = new DelegateCommand(ToggleSpeakerCaptureAsync);
        ToggleMicCaptureCommand = new DelegateCommand(ToggleMicCaptureAsync);
        ToggleOutputModeCommand = new DelegateCommand(ToggleOutputModeAsync, () => IsDeviceSelectionEnabled);
        OpenSettingsCommand = new DelegateCommand(OpenSettingsAsync, () => IsDeviceSelectionEnabled);
        OpenLibraryCommand = new DelegateCommand(OpenLibraryAsync);

        _recordingService.StateChanged += (_, s) => RunOnUi(() =>
        {
            StateText = $"状態: {s}";
            if (s is RecordingState.Stopped or RecordingState.Error)
            {
                ResetLevelMeters();
            }
            OnPropertyChanged(nameof(StartStopButtonText));
            OnPropertyChanged(nameof(PauseResumeButtonText));
            OnPropertyChanged(nameof(IsDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsStoppedOrError));
            OnPropertyChanged(nameof(IsProcessSelectionEnabled));
            OnPropertyChanged(nameof(RecordButtonVisibility));
            OnPropertyChanged(nameof(RecordingControlsVisibility));
            OnPropertyChanged(nameof(PauseGlyphVisibility));
            OnPropertyChanged(nameof(ResumeGlyphVisibility));
            RefreshCommands();
        });

        _recordingService.ErrorOccurred += (_, e) => RunOnUi(() => LastErrorText = $"エラー: {e}");
        _recordingService.OutputSourceChanged += (_, e) => RunOnUi(() => LastErrorText = $"出力切替: {e.Previous} -> {e.Current} ({e.Reason})");
        _recordingService.StatisticsUpdated += (_, st) => RunOnUi(() =>
        {
            OutputPathText = $"出力: {st.OutputFilePath ?? "-"}";
            ElapsedText = st.ElapsedTime.ToString(@"hh\:mm\:ss");
            SpeakerLevelPercent = IsSpeakerCaptureEnabled ? ConvertLevelToPercent(st.SpeakerLevel) : 0;
            MicLevelPercent = IsMicCaptureEnabled ? ConvertLevelToPercent(st.MicLevel) : 0;
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
    public DelegateCommand ToggleSpeakerCaptureCommand { get; }
    public DelegateCommand ToggleMicCaptureCommand { get; }
    public DelegateCommand ToggleOutputModeCommand { get; }
    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenLibraryCommand { get; }

    public ObservableCollection<AudioDeviceInfo> SpeakerDevices { get; }
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; }
    public ObservableCollection<ProcessListItem> ProcessItems { get; }

    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }
    public string OutputPathText { get => _outputPathText; private set => SetField(ref _outputPathText, value); }
    public string MetricsText { get => _metricsText; private set => SetField(ref _metricsText, value); }
    public string LastErrorText { get => _lastErrorText; private set => SetField(ref _lastErrorText, value); }
    public string ElapsedText { get => _elapsedText; private set => SetField(ref _elapsedText, value); }
    public double SpeakerLevelPercent
    {
        get => _speakerLevelPercent;
        private set
        {
            if (SetField(ref _speakerLevelPercent, value))
            {
                OnPropertyChanged(nameof(SpeakerIconBrush));
            }
        }
    }

    public double MicLevelPercent
    {
        get => _micLevelPercent;
        private set
        {
            if (SetField(ref _micLevelPercent, value))
            {
                OnPropertyChanged(nameof(MicIconBrush));
            }
        }
    }

    public string SelectedSpeakerDeviceId
    {
        get => _selectedSpeakerDeviceId;
        set
        {
            if (SetField(ref _selectedSpeakerDeviceId, value))
            {
                OnPropertyChanged(nameof(SelectedSpeakerDeviceName));
                if (!_isRefreshingDeviceList)
                {
                    IsSpeakerDevicePopupOpenNormal = false;
                    IsSpeakerDevicePopupOpenMini = false;
                }
            }
        }
    }

    public string SelectedMicDeviceId
    {
        get => _selectedMicDeviceId;
        set
        {
            if (SetField(ref _selectedMicDeviceId, value))
            {
                OnPropertyChanged(nameof(SelectedMicDeviceName));
                if (!_isRefreshingDeviceList)
                {
                    IsMicDevicePopupOpenNormal = false;
                    IsMicDevicePopupOpenMini = false;
                }
            }
        }
    }

    public string AlignmentMillisecondsText { get => _alignmentMillisecondsText; set => SetField(ref _alignmentMillisecondsText, value); }
    public string StartStopHotkeyText { get => _startStopHotkeyText; private set => SetField(ref _startStopHotkeyText, value); }

    public bool IsSpeakerCaptureEnabled
    {
        get => _isSpeakerCaptureEnabled;
        private set
        {
            if (SetField(ref _isSpeakerCaptureEnabled, value))
            {
                _recordingService.SetSpeakerCaptureEnabled(value);
                if (!value)
                {
                    SpeakerLevelPercent = 0;
                }

                OnPropertyChanged(nameof(SpeakerMuteSlashVisibility));
                OnPropertyChanged(nameof(SpeakerIconBrush));
            }
        }
    }

    public bool IsMicCaptureEnabled
    {
        get => _isMicCaptureEnabled;
        private set
        {
            if (SetField(ref _isMicCaptureEnabled, value))
            {
                _recordingService.SetMicCaptureEnabled(value);
                if (!value)
                {
                    MicLevelPercent = 0;
                }

                OnPropertyChanged(nameof(MicMuteSlashVisibility));
                OnPropertyChanged(nameof(MicIconBrush));
            }
        }
    }

    public Brush SpeakerIconBrush => BuildIconBrush(IsSpeakerCaptureEnabled, SpeakerLevelPercent, Colors.DeepSkyBlue);
    public Brush MicIconBrush => BuildIconBrush(IsMicCaptureEnabled, MicLevelPercent, Color.FromRgb(54, 224, 98));
    public Visibility SpeakerMuteSlashVisibility => IsSpeakerCaptureEnabled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MicMuteSlashVisibility => IsMicCaptureEnabled ? Visibility.Collapsed : Visibility.Visible;
    public string SelectedSpeakerDeviceName => SpeakerDevices.FirstOrDefault(x => x.DeviceId == SelectedSpeakerDeviceId)?.FriendlyName ?? "スピーカーデバイス未選択";
    public string SelectedMicDeviceName => MicDevices.FirstOrDefault(x => x.DeviceId == SelectedMicDeviceId)?.FriendlyName ?? "マイクデバイス未選択";
    public string SelectedOutputModeName => SelectedOutputMode == OutputCaptureMode.ProcessLoopback ? "プログラムモード" : "スピーカーモード";
    public bool IsProgramMode => SelectedOutputMode == OutputCaptureMode.ProcessLoopback;
    public bool IsSpeakerMode => !IsProgramMode;
    public string SelectedProcessDisplayName => SelectedProcessItem?.DisplayText ?? "プロセス未選択";

    public bool IsSpeakerDevicePopupOpenNormal
    {
        get => _isSpeakerDevicePopupOpenNormal;
        set
        {
            if (SetField(ref _isSpeakerDevicePopupOpenNormal, value) && value)
            {
                _ = LoadDevicesAsync();
            }
        }
    }

    public bool IsSpeakerDevicePopupOpenMini
    {
        get => _isSpeakerDevicePopupOpenMini;
        set
        {
            if (SetField(ref _isSpeakerDevicePopupOpenMini, value) && value)
            {
                _ = LoadDevicesAsync();
            }
        }
    }

    public bool IsMicDevicePopupOpenNormal
    {
        get => _isMicDevicePopupOpenNormal;
        set
        {
            if (SetField(ref _isMicDevicePopupOpenNormal, value) && value)
            {
                _ = LoadDevicesAsync();
            }
        }
    }

    public bool IsMicDevicePopupOpenMini
    {
        get => _isMicDevicePopupOpenMini;
        set
        {
            if (SetField(ref _isMicDevicePopupOpenMini, value) && value)
            {
                _ = LoadDevicesAsync();
            }
        }
    }

    public bool IsProcessPopupOpenNormal
    {
        get => _isProcessPopupOpenNormal;
        set
        {
            if (SetField(ref _isProcessPopupOpenNormal, value) && value && IsProcessSelectionEnabled)
            {
                _ = LoadProcessesAsync();
            }
        }
    }

    public bool IsProcessPopupOpenMini
    {
        get => _isProcessPopupOpenMini;
        set
        {
            if (SetField(ref _isProcessPopupOpenMini, value) && value && IsProcessSelectionEnabled)
            {
                _ = LoadProcessesAsync();
            }
        }
    }

    public OutputCaptureMode SelectedOutputMode
    {
        get => _selectedOutputMode;
        set
        {
            if (SetField(ref _selectedOutputMode, value))
            {
                OnPropertyChanged(nameof(IsProcessSelectionEnabled));
                OnPropertyChanged(nameof(SelectedOutputModeName));
                OnPropertyChanged(nameof(IsProgramMode));
                OnPropertyChanged(nameof(IsSpeakerMode));
                if (value != OutputCaptureMode.ProcessLoopback)
                {
                    IsProcessPopupOpenNormal = false;
                    IsProcessPopupOpenMini = false;
                }
                RefreshCommands();
            }
        }
    }

    public ProcessListItem? SelectedProcessItem
    {
        get => _selectedProcessItem;
        set
        {
            if (SetField(ref _selectedProcessItem, value))
            {
                OnPropertyChanged(nameof(SelectedProcessDisplayName));
                IsProcessPopupOpenNormal = false;
                IsProcessPopupOpenMini = false;
            }
        }
    }

    public bool IsMiniMode
    {
        get => _isMiniMode;
        private set
        {
            if (SetField(ref _isMiniMode, value))
            {
                OnPropertyChanged(nameof(DetailsVisibility));
                OnPropertyChanged(nameof(NormalHeaderVisibility));
                OnPropertyChanged(nameof(MiniHeaderVisibility));
                OnPropertyChanged(nameof(WindowWidth));
                OnPropertyChanged(nameof(WindowHeight));
                OnPropertyChanged(nameof(MiniModeButtonText));
                OnPropertyChanged(nameof(MiniModeGlyph));
            }
        }
    }

    public Visibility DetailsVisibility => IsMiniMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NormalHeaderVisibility => IsMiniMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MiniHeaderVisibility => IsMiniMode ? Visibility.Visible : Visibility.Collapsed;
    public double WindowWidth => IsMiniMode ? 640 : 760;
    public double WindowHeight => IsMiniMode ? 145 : 145;
    public bool IsStoppedOrError => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error;
    public bool IsDeviceSelectionEnabled => IsStoppedOrError;
    public bool IsProcessSelectionEnabled => IsDeviceSelectionEnabled && SelectedOutputMode == OutputCaptureMode.ProcessLoopback;
    public Visibility RecordButtonVisibility => IsStoppedOrError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecordingControlsVisibility => _recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility PauseGlyphVisibility => _recordingService.CurrentState == RecordingState.Recording
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ResumeGlyphVisibility => _recordingService.CurrentState == RecordingState.Paused
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string StartStopButtonText => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error ? "録音開始" : "停止";
    public string PauseResumeButtonText => _recordingService.CurrentState == RecordingState.Paused ? "再開" : "一時停止";
    public string MiniModeButtonText => IsMiniMode ? "通常表示に切替" : "ミニ表示に切替";
    public string MiniModeGlyph => IsMiniMode ? "\uE73F" : "\uE740";

    private async Task LoadDevicesAsync()
    {
        try
        {
            var speakers = await _deviceService.GetSpeakerDevicesAsync();
            var mics = await _deviceService.GetMicrophoneDevicesAsync();
            var speakerOptions = BuildDeviceOptions(speakers, DeviceKind.Speaker);
            var micOptions = BuildDeviceOptions(mics, DeviceKind.Microphone);

            RunOnUi(() =>
            {
                _isRefreshingDeviceList = true;
                try
                {
                    SpeakerDevices.Clear();
                    foreach (var d in speakerOptions)
                    {
                        SpeakerDevices.Add(d);
                    }

                    MicDevices.Clear();
                    foreach (var d in micOptions)
                    {
                        MicDevices.Add(d);
                    }

                    if (string.IsNullOrWhiteSpace(SelectedSpeakerDeviceId) || !SpeakerDevices.Any(x => x.DeviceId == SelectedSpeakerDeviceId))
                    {
                        SelectedSpeakerDeviceId = SystemDefaultDeviceId;
                    }

                    if (string.IsNullOrWhiteSpace(SelectedMicDeviceId) || !MicDevices.Any(x => x.DeviceId == SelectedMicDeviceId))
                    {
                        SelectedMicDeviceId = SystemDefaultDeviceId;
                    }

                    OnPropertyChanged(nameof(SelectedSpeakerDeviceName));
                    OnPropertyChanged(nameof(SelectedMicDeviceName));
                }
                finally
                {
                    _isRefreshingDeviceList = false;
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
                OnPropertyChanged(nameof(SelectedProcessDisplayName));
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

            alignmentMs = Math.Clamp(alignmentMs, -1000, 1000);
            AlignmentMillisecondsText = alignmentMs.ToString();

            _options = EnsureDefaults(_options) with
            {
                SpeakerDeviceId = SelectedSpeakerDeviceId,
                MicDeviceId = SelectedMicDeviceId,
                OutputCaptureMode = mode,
                TargetProcessId = targetPid,
                ChannelAlignmentMilliseconds = alignmentMs
            };

            var resolvedSpeakerDeviceId = await ResolveDeviceIdAsync(_options.SpeakerDeviceId, DeviceKind.Speaker);
            var resolvedMicDeviceId = await ResolveDeviceIdAsync(_options.MicDeviceId, DeviceKind.Microphone);
            if (string.IsNullOrWhiteSpace(resolvedSpeakerDeviceId) || string.IsNullOrWhiteSpace(resolvedMicDeviceId))
            {
                LastErrorText = "システム既定のデバイス解決に失敗しました。デバイス設定を確認してください。";
                return;
            }

            var startOptions = _options with
            {
                SpeakerDeviceId = resolvedSpeakerDeviceId,
                MicDeviceId = resolvedMicDeviceId
            };

            await _settingsService.SaveRecordingOptionsAsync(_options);
            var path = await _recordingService.StartAsync(startOptions);
            _recordingService.SetSpeakerCaptureEnabled(IsSpeakerCaptureEnabled);
            _recordingService.SetMicCaptureEnabled(IsMicCaptureEnabled);
            OutputPathText = $"出力: {path}";
            return;
        }

        await _recordingService.StopAsync();
        ResetLevelMeters();
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

    private Task ToggleSpeakerCaptureAsync()
    {
        IsSpeakerCaptureEnabled = !IsSpeakerCaptureEnabled;
        return Task.CompletedTask;
    }

    private Task ToggleMicCaptureAsync()
    {
        IsMicCaptureEnabled = !IsMicCaptureEnabled;
        return Task.CompletedTask;
    }

    private Task ToggleOutputModeAsync()
    {
        SelectedOutputMode = SelectedOutputMode == OutputCaptureMode.ProcessLoopback
            ? OutputCaptureMode.SpeakerLoopback
            : OutputCaptureMode.ProcessLoopback;

        return Task.CompletedTask;
    }

    private async Task OpenLibraryAsync()
    {
        try
        {
            if (_libraryWindow is not null)
            {
                _libraryWindow.Activate();
                return;
            }

            var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive");
            var dbPath = Path.Combine(appDir, "library.db");
            var vm = new LibraryViewModel(new RecordingCatalogService(dbPath), EnsureDefaults(_options).OutputDirectory);
            _libraryWindow = new LibraryWindow(vm)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            _libraryWindow.Closed += (_, _) => _libraryWindow = null;
            _libraryWindow.Show();
        }
        catch (Exception ex)
        {
            LastErrorText = $"ライブラリ起動失敗: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            AlignmentMilliseconds = _options.ChannelAlignmentMilliseconds,
            StartStopHotkeyText = _options.StartStopHotkey,
            OutputDirectory = _options.OutputDirectory
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var normalizedOffset = Math.Clamp(dialog.AlignmentMilliseconds, -1000, 1000);
        var normalizedOutput = string.IsNullOrWhiteSpace(dialog.OutputDirectory)
            ? EnsureDefaults(_options).OutputDirectory
            : dialog.OutputDirectory;
        if (!KeyboardShortcutHelper.TryParseAndNormalize(dialog.StartStopHotkeyText, out _, out var normalizedHotkey))
        {
            normalizedHotkey = KeyboardShortcutHelper.DefaultStartStopHotkey;
        }

        _options = EnsureDefaults(_options) with
        {
            ChannelAlignmentMilliseconds = normalizedOffset,
            OutputDirectory = normalizedOutput,
            StartStopHotkey = normalizedHotkey
        };

        AlignmentMillisecondsText = normalizedOffset.ToString();
        StartStopHotkeyText = normalizedHotkey;
        await _settingsService.SaveRecordingOptionsAsync(_options);
        LastErrorText = string.Empty;
    }
    private Task ToggleMiniModeAsync()
    {
        IsSpeakerDevicePopupOpenNormal = false;
        IsSpeakerDevicePopupOpenMini = false;
        IsMicDevicePopupOpenNormal = false;
        IsMicDevicePopupOpenMini = false;
        IsProcessPopupOpenNormal = false;
        IsProcessPopupOpenMini = false;
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

    private static IReadOnlyList<AudioDeviceInfo> BuildDeviceOptions(IReadOnlyList<AudioDeviceInfo> devices, DeviceKind kind)
    {
        var defaultName = devices.FirstOrDefault(x => x.IsDefault)?.FriendlyName ?? "デバイス未検出";
        var options = new List<AudioDeviceInfo>(devices.Count + 1)
        {
            new(SystemDefaultDeviceId, $"システム既定 ({defaultName})", true, kind)
        };
        options.AddRange(devices);
        return options;
    }

    private async Task<string> ResolveDeviceIdAsync(string selectedDeviceId, DeviceKind kind)
    {
        if (selectedDeviceId != SystemDefaultDeviceId)
        {
            return selectedDeviceId;
        }

        var defaultDevice = kind == DeviceKind.Speaker
            ? await _deviceService.GetDefaultSpeakerDeviceAsync()
            : await _deviceService.GetDefaultMicrophoneDeviceAsync();

        return defaultDevice?.DeviceId ?? string.Empty;
    }
    private void RefreshCommands()
    {
        StartStopCommand.RaiseCanExecuteChanged();
        PauseResumeCommand.RaiseCanExecuteChanged();
        ToggleMiniModeCommand.RaiseCanExecuteChanged();
        ToggleOutputModeCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        OpenLibraryCommand.RaiseCanExecuteChanged();
        RefreshProcessesCommand.RaiseCanExecuteChanged();
    }

    private static RecordingOptions EnsureDefaults(RecordingOptions options)
    {
        var output = options.OutputDirectory;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoxArchive");
        }

        var hotkey = options.StartStopHotkey;
        if (!KeyboardShortcutHelper.TryParseAndNormalize(hotkey, out _, out var normalizedHotkey))
        {
            normalizedHotkey = KeyboardShortcutHelper.DefaultStartStopHotkey;
        }

        return options with
        {
            OutputDirectory = output,
            StartStopHotkey = normalizedHotkey
        };
    }

    private static double ConvertLevelToPercent(double linearLevel)
    {
        var clamped = Math.Clamp(linearLevel, 0d, 1d);
        if (clamped <= 0d)
        {
            return 0d;
        }

        var db = (20d * Math.Log10(Math.Max(clamped, 1e-6d))) + MeterDisplayGainDb;
        var normalized = (db - MeterFloorDb) / (MeterCeilingDb - MeterFloorDb);
        return Math.Clamp(normalized * 100d, 0d, 100d);
    }
    private void ResetLevelMeters()
    {
        SpeakerLevelPercent = 0d;
        MicLevelPercent = 0d;
    }

    private static Brush BuildIconBrush(bool isEnabled, double levelPercent, Color accent)
    {
        _ = levelPercent;
        _ = accent;

        if (!isEnabled)
        {
            return new SolidColorBrush(Color.FromRgb(122, 134, 149));
        }

        return new SolidColorBrush(Color.FromRgb(210, 216, 225));
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
