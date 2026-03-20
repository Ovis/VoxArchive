using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Runtime;

namespace VoxArchive.Wpf;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRecordingService _recordingService;

    private readonly ISettingsService _settingsService;
    private readonly IDeviceService _deviceService;
    private readonly IProcessCatalogService _processCatalogService;
    private RecordingOptions _options;
    private string _selectedSpeakerDeviceId = string.Empty;
    private string _selectedMicDeviceId = string.Empty;
    private OutputCaptureMode _selectedOutputMode;
    private string _elapsedText = "00:00:00";
    private double _speakerLevelPercent;
    private double _micLevelPercent;
    private string _startStopHotkeyText = KeyboardShortcutHelper.DefaultStartStopHotkey;
    private ProcessListItem? _selectedProcessItem;
    private bool _isSpeakerCaptureEnabled = true;
    private bool _isMicCaptureEnabled = true;
    private bool _isSpeakerDevicePopupOpenNormal;
    private bool _isMicDevicePopupOpenNormal;
    private bool _isProcessPopupOpenNormal;
    private bool _isMiniMode;
    private bool _isRefreshingDeviceList;
    private readonly RecordingCatalogService _libraryCatalogService;
    private readonly WhisperModelStore _whisperModelStore;
    private readonly WhisperTranscriptionService _whisperTranscriptionService;
    private readonly TranscriptionJobQueue _transcriptionQueue;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private string? _lastRecordedFilePath;
    private LibraryWindow? _libraryWindow;
    private LibraryViewModel? _libraryViewModel;

    private const double MeterFloorDb = -60d;
    private const double NormalWindowWidth = 510d;
    private const double RecordingWindowWidth = 590d;
    private const double MeterCeilingDb = 0d;
    private const double MeterDisplayGainDb = 6d;
    private const string SystemDefaultDeviceId = "__system_default__";
    private const int LibraryRegisterMaxRetry = 10;
    private const int LibraryRegisterRetryDelayMilliseconds = 100;

    public MainViewModel(
        RecordingRuntimeContext context,
        RecordingCatalogService libraryCatalogService,
        WhisperModelStore whisperModelStore,
        WhisperTranscriptionService whisperTranscriptionService,
        TranscriptionJobQueue transcriptionQueue,
        ILogger<MainViewModel> logger,
        IServiceProvider serviceProvider)
    {
        _recordingService = context.RecordingService;
        _settingsService = context.SettingsService;
        _deviceService = context.DeviceService;
        _processCatalogService = context.ProcessCatalogService;
        _options = EnsureDefaults(context.DefaultOptions);
        _libraryCatalogService = libraryCatalogService;
        _whisperModelStore = whisperModelStore;
        _whisperTranscriptionService = whisperTranscriptionService;
        _transcriptionQueue = transcriptionQueue;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _transcriptionQueue.JobCompleted += OnTranscriptionJobCompleted;

        SpeakerDevices = new ObservableCollection<AudioDeviceInfo>();
        MicDevices = new ObservableCollection<AudioDeviceInfo>();
        ProcessItems = new ObservableCollection<ProcessListItem>();

        _selectedSpeakerDeviceId = _options.SpeakerDeviceId;
        _selectedMicDeviceId = _options.MicDeviceId;
        _selectedOutputMode = _options.OutputCaptureMode;
        _startStopHotkeyText = _options.StartStopHotkey;
        _isSpeakerCaptureEnabled = _recordingService.IsSpeakerCaptureEnabled;
        _isMicCaptureEnabled = _recordingService.IsMicCaptureEnabled;

        StartStopCommand = new DelegateCommand(StartOrStopAsync, CanStartOrStop);
        PauseResumeCommand = new DelegateCommand(PauseOrResumeAsync, CanPauseOrResume);
        RefreshProcessesCommand = new DelegateCommand(LoadProcessesAsync, () => IsProcessSelectionEnabled);
        ToggleSpeakerCaptureCommand = new DelegateCommand(ToggleSpeakerCaptureAsync);
        ToggleMicCaptureCommand = new DelegateCommand(ToggleMicCaptureAsync);
        ToggleOutputModeCommand = new DelegateCommand(ToggleOutputModeAsync, () => IsDeviceSelectionEnabled);
        ToggleWindowModeCommand = new DelegateCommand(ToggleWindowModeAsync);
        OpenSettingsCommand = new DelegateCommand(OpenSettingsAsync, () => IsDeviceSelectionEnabled);
        OpenLibraryCommand = new DelegateCommand(OpenLibraryAsync);

        _recordingService.StateChanged += OnRecordingStateChanged;
        _recordingService.ErrorOccurred += OnRecordingErrorOccurred;
        _recordingService.OutputSourceChanged += OnOutputSourceChanged;
        _recordingService.StatisticsUpdated += OnStatisticsUpdated;

        _ = LoadDevicesAsync();
        _ = LoadProcessesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DelegateCommand StartStopCommand { get; }
    public DelegateCommand PauseResumeCommand { get; }
    public DelegateCommand RefreshProcessesCommand { get; }
    public DelegateCommand ToggleSpeakerCaptureCommand { get; }
    public DelegateCommand ToggleMicCaptureCommand { get; }
    public DelegateCommand ToggleOutputModeCommand { get; }
    public DelegateCommand ToggleWindowModeCommand { get; }
    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenLibraryCommand { get; }

    public ObservableCollection<AudioDeviceInfo> SpeakerDevices { get; }
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; }
    public ObservableCollection<ProcessListItem> ProcessItems { get; }
    public string ElapsedText { get => _elapsedText; private set => SetField(ref _elapsedText, value); }
    public double SpeakerLevelPercent
    {
        get => _speakerLevelPercent;
        private set
        {
            if (SetField(ref _speakerLevelPercent, value))
            {
                OnPropertyChanged(nameof(SpeakerIconBrush));
                OnPropertyChanged(nameof(SpeakerRingBrush));
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
                OnPropertyChanged(nameof(MicRingBrush));
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
                }
            }
        }
    }
    public string StartStopHotkeyText { get => _startStopHotkeyText; private set => SetField(ref _startStopHotkeyText, value); }
    public bool SuppressCloseToTrayNotice => _options.SuppressCloseToTrayNotice;

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
                OnPropertyChanged(nameof(SpeakerRingBrush));
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
                OnPropertyChanged(nameof(MicRingBrush));
            }
        }
    }

    public Brush SpeakerIconBrush => BuildIconBrush(IsSpeakerCaptureEnabled, SpeakerLevelPercent, Colors.DeepSkyBlue);
    public Brush MicIconBrush => BuildIconBrush(IsMicCaptureEnabled, MicLevelPercent, Color.FromRgb(54, 224, 98));
    public Brush SpeakerRingBrush => BuildRingBrush(IsSpeakerCaptureEnabled, SpeakerLevelPercent, Color.FromRgb(0, 191, 255));
    public Brush MicRingBrush => BuildRingBrush(IsMicCaptureEnabled, MicLevelPercent, Color.FromRgb(54, 224, 98));
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



    public bool IsMiniMode
    {
        get => _isMiniMode;
        private set
        {
            if (!SetField(ref _isMiniMode, value))
            {
                return;
            }

            if (value)
            {
                IsSpeakerDevicePopupOpenNormal = false;
                IsMicDevicePopupOpenNormal = false;
                IsProcessPopupOpenNormal = false;
            }

            OnPropertyChanged(nameof(WindowWidth));
            OnPropertyChanged(nameof(WindowHeight));
            OnPropertyChanged(nameof(NormalMainControlsVisibility));
            OnPropertyChanged(nameof(MiniMainControlsVisibility));
            OnPropertyChanged(nameof(WindowModeGlyph));
            OnPropertyChanged(nameof(WindowModeToolTip));
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
                OnPropertyChanged(nameof(IsSpeakerDeviceSelectionEnabled));
                OnPropertyChanged(nameof(SelectedOutputModeName));
                OnPropertyChanged(nameof(IsProgramMode));
                OnPropertyChanged(nameof(IsSpeakerMode));
                if (value != OutputCaptureMode.ProcessLoopback)
                {
                    IsProcessPopupOpenNormal = false;
                }
                EnsureSpeakerDevicePopupState();
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
            }
        }
    }


    public double WindowWidth => IsMiniMode ? 320 : (_recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused ? RecordingWindowWidth : NormalWindowWidth);
    public double WindowHeight => 100;
    public Visibility NormalMainControlsVisibility => IsMiniMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MiniMainControlsVisibility => IsMiniMode ? Visibility.Visible : Visibility.Collapsed;
    public string WindowModeGlyph => IsMiniMode ? "\uE73F" : "\uE740";
    public string WindowModeToolTip => IsMiniMode ? "通常モード" : "ミニモード";
    public bool IsStoppedOrError => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error;
    public bool IsDeviceSelectionEnabled => IsStoppedOrError;
    public bool IsSpeakerDeviceSelectionEnabled =>
        !(SelectedOutputMode == OutputCaptureMode.ProcessLoopback &&
          _recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused);
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
            RunOnUi(() => _logger.LogWarning(ex, "デバイス列挙失敗"));
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
            RunOnUi(() => _logger.LogWarning(ex, "プロセス列挙失敗"));
        }
    }

    private async Task StartOrStopAsync()
    {
        if (_recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error)
        {
            if (!FfmpegRuntimeChecker.IsAvailable(out var ffmpegDetail))
            {
                var message = BuildFfmpegMissingMessage(ffmpegDetail);
                _logger.LogWarning("録音開始前に ffmpeg 未検出: {Detail}", ffmpegDetail);
                ModernDialog.Show(
                    message,
                    "ffmpeg 未検出",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var mode = SelectedOutputMode;
            var targetPid = SelectedProcessItem?.ProcessId;

            if (mode == OutputCaptureMode.ProcessLoopback)
            {
                if (targetPid is null || !await _processCatalogService.ExistsAsync(targetPid.Value))
                {
                    var result = ModernDialog.Show(
                        "選択したアプリは現在起動していません。\nスピーカー録音に切り替えて開始しますか？",
                        "録音開始確認",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question,
                        MessageBoxResult.OK);

                    if (result != MessageBoxResult.OK)
                    {
                        _logger.LogWarning("録音開始をキャンセルしました。");
                        return;
                    }

                    mode = OutputCaptureMode.SpeakerLoopback;
                    targetPid = null;
                    SelectedOutputMode = OutputCaptureMode.SpeakerLoopback;
                }
            }

            var alignmentMs = Math.Clamp(_options.ChannelAlignmentMilliseconds, -1000, 1000);
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
                _logger.LogWarning("システム既定のデバイス解決に失敗しました。デバイス設定を確認してください。");
                return;
            }

            var startOptions = _options with
            {
                SpeakerDeviceId = resolvedSpeakerDeviceId,
                MicDeviceId = resolvedMicDeviceId
            };

            await _settingsService.SaveRecordingOptionsAsync(_options);
            var path = await _recordingService.StartAsync(startOptions);
            _lastRecordedFilePath = path;
            _recordingService.SetSpeakerCaptureEnabled(IsSpeakerCaptureEnabled);
            _recordingService.SetMicCaptureEnabled(IsMicCaptureEnabled);
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


    private Task ToggleWindowModeAsync()
    {
        IsMiniMode = !IsMiniMode;
        return Task.CompletedTask;
    }
    
    public async Task SetSuppressCloseToTrayNoticeAsync(bool suppress)
    {
        if (_options.SuppressCloseToTrayNotice == suppress)
        {
            return;
        }

        _options = EnsureDefaults(_options) with
        {
            SuppressCloseToTrayNotice = suppress
        };

        try
        {
            await _settingsService.SaveRecordingOptionsAsync(_options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "終了時ガイド設定の保存に失敗");
        }
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

            var vm = ActivatorUtilities.CreateInstance<LibraryViewModel>(
                _serviceProvider,
                _libraryCatalogService,
                _transcriptionQueue,
                () => _options,
                _options.DefaultSpeakerPlaybackGainDb,
                _options.DefaultMicPlaybackGainDb);
            _libraryViewModel = vm;
            _libraryWindow = new LibraryWindow(vm);
            _libraryWindow.Closed += (_, _) =>
            {
                _libraryWindow = null;
                _libraryViewModel = null;
            };
            _libraryWindow.Show();
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ライブラリ起動失敗");
            ModernDialog.Show(
                $"ライブラリウィンドウの表示に失敗しました。\n{ex.Message}",
                "ライブラリ起動失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        await Task.CompletedTask;
    }

    private async Task RegisterLatestRecordingAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastRecordedFilePath))
        {
            return;
        }

        var filePath = _lastRecordedFilePath;
        for (var attempt = 0; attempt < LibraryRegisterMaxRetry; attempt++)
        {
            try
            {
                if (_libraryViewModel is not null)
                {
                    await _libraryViewModel.ReloadAsync(filePath);
                }
                else
                {
                    await _libraryCatalogService.AddOrUpdateFileAsync(filePath);
                }

                _lastRecordedFilePath = null;
                TryEnqueueAutoTranscription(filePath);
                return;
            }
            catch (FileNotFoundException) when (attempt < LibraryRegisterMaxRetry - 1)
            {
                await Task.Delay(LibraryRegisterRetryDelayMilliseconds);
            }
            catch (IOException) when (attempt < LibraryRegisterMaxRetry - 1)
            {
                await Task.Delay(LibraryRegisterRetryDelayMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ライブラリ登録失敗");
                return;
            }
        }

        _logger.LogWarning("ライブラリ登録失敗: 録音ファイルが見つかりません。");
    }


    private void TryEnqueueAutoTranscription(string filePath)
    {
        if (!_options.TranscriptionEnabled || !_options.AutoTranscriptionAfterRecord)
        {
            return;
        }

        var enqueued = _transcriptionQueue.TryEnqueue(new TranscriptionJobRequest(
            AudioFilePath: filePath,
            Options: _options,
            Trigger: TranscriptionTrigger.AutoAfterRecord));

        if (!enqueued)
        {
            _logger.LogWarning("文字起こしキューへの投入に失敗しました。");
            return;
        }

        if (_options.TranscriptionToastNotificationEnabled)
        {
            AppNotificationHub.Notify("VoxArchive", $"自動文字起こし開始: {Path.GetFileName(filePath)}", System.Windows.Forms.ToolTipIcon.Info);
        }
    }
    private async Task OpenSettingsAsync()
    {
        try
        {
            var dialog = new SettingsWindow(_whisperModelStore, _whisperTranscriptionService)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                AlignmentMilliseconds = _options.ChannelAlignmentMilliseconds,
                StartStopHotkeyText = _options.StartStopHotkey,
                OutputDirectory = _options.OutputDirectory,
                RecordingMetricsLogEnabled = _options.RecordingMetricsLogEnabled,
                DefaultSpeakerPlaybackGainDb = _options.DefaultSpeakerPlaybackGainDb,
                DefaultMicPlaybackGainDb = _options.DefaultMicPlaybackGainDb,
                TranscriptionEnabled = _options.TranscriptionEnabled,
                AutoTranscriptionAfterRecord = _options.AutoTranscriptionAfterRecord,
                TranscriptionExecutionMode = _options.TranscriptionExecutionMode,
                TranscriptionModel = _options.TranscriptionModel,
                TranscriptionLanguage = _options.TranscriptionLanguage,
                TranscriptionOutputFormats = _options.TranscriptionOutputFormats,
                AutoTranscriptionPriority = _options.AutoTranscriptionPriority,
                ManualTranscriptionPriority = _options.ManualTranscriptionPriority,
                TranscriptionToastNotificationEnabled = _options.TranscriptionToastNotificationEnabled
            };


            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var normalizedOffset = Math.Clamp(dialog.AlignmentMilliseconds, -1000, 1000);
            var normalizedOutput = string.IsNullOrWhiteSpace(dialog.OutputDirectory)
                ? EnsureDefaults(_options).OutputDirectory
                : dialog.OutputDirectory;
            var normalizedSpeakerGain = Math.Clamp(dialog.DefaultSpeakerPlaybackGainDb, -60d, 48d);
            var normalizedMicGain = Math.Clamp(dialog.DefaultMicPlaybackGainDb, -60d, 48d);

            if (!KeyboardShortcutHelper.TryParseAndNormalize(dialog.StartStopHotkeyText, out _, out var normalizedHotkey))
            {
                normalizedHotkey = KeyboardShortcutHelper.DefaultStartStopHotkey;
            }

            var normalizedLanguage = string.IsNullOrWhiteSpace(dialog.TranscriptionLanguage)
                ? "ja"
                : dialog.TranscriptionLanguage.Trim();
            var normalizedFormats = dialog.TranscriptionOutputFormats == TranscriptionOutputFormats.None
                ? TranscriptionOutputFormats.Txt
                : dialog.TranscriptionOutputFormats;

            _options = EnsureDefaults(_options) with
            {
                ChannelAlignmentMilliseconds = normalizedOffset,
                OutputDirectory = normalizedOutput,
                StartStopHotkey = normalizedHotkey,
                DefaultSpeakerPlaybackGainDb = normalizedSpeakerGain,
                DefaultMicPlaybackGainDb = normalizedMicGain,
                RecordingMetricsLogEnabled = dialog.RecordingMetricsLogEnabled,
                TranscriptionEnabled = dialog.TranscriptionEnabled,
                AutoTranscriptionAfterRecord = dialog.AutoTranscriptionAfterRecord,
                TranscriptionExecutionMode = dialog.TranscriptionExecutionMode,
                TranscriptionModel = dialog.TranscriptionModel,
                TranscriptionLanguage = normalizedLanguage,
                TranscriptionOutputFormats = normalizedFormats,
                AutoTranscriptionPriority = dialog.AutoTranscriptionPriority,
                ManualTranscriptionPriority = dialog.ManualTranscriptionPriority,
                TranscriptionToastNotificationEnabled = dialog.TranscriptionToastNotificationEnabled
            };
            StartStopHotkeyText = normalizedHotkey;
            await _settingsService.SaveRecordingOptionsAsync(_options);
            _libraryViewModel?.NotifyOptionsChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定画面起動失敗");
            ModernDialog.Show(
                $"設定画面の表示に失敗しました。\n{ex.Message}",
                "設定画面起動失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

    private void OnRecordingStateChanged(object? sender, RecordingState state)
    {
        RunOnUi(() =>
        {
            if (state is RecordingState.Stopped or RecordingState.Error)
            {
                ResetLevelMeters();
            }

            if (state == RecordingState.Stopped)
            {
                _ = RegisterLatestRecordingAsync();
            }

            OnPropertyChanged(nameof(IsDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsSpeakerDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsStoppedOrError));
            OnPropertyChanged(nameof(IsProcessSelectionEnabled));
            OnPropertyChanged(nameof(RecordButtonVisibility));
            OnPropertyChanged(nameof(RecordingControlsVisibility));
            OnPropertyChanged(nameof(WindowWidth));
            OnPropertyChanged(nameof(PauseGlyphVisibility));
            OnPropertyChanged(nameof(ResumeGlyphVisibility));
            EnsureSpeakerDevicePopupState();
            RefreshCommands();
        });
    }

    private void OnRecordingErrorOccurred(object? sender, string message)
    {
        RunOnUi(() => _logger.LogWarning("エラー: {Message}", message));
    }

    private void OnOutputSourceChanged(object? sender, OutputSourceChangedEvent e)
    {
        RunOnUi(() => _logger.LogWarning("出力切替: {Previous} -> {Current} ({Reason})", e.Previous, e.Current, e.Reason));
    }

    private void OnStatisticsUpdated(object? sender, RecordingStatistics st)
    {
        RunOnUi(() =>
        {
            if (!string.IsNullOrWhiteSpace(st.OutputFilePath))
            {
                _lastRecordedFilePath = st.OutputFilePath;
            }

            ElapsedText = st.ElapsedTime.ToString(@"hh\:mm\:ss");
            SpeakerLevelPercent = IsSpeakerCaptureEnabled ? ConvertLevelToPercent(st.SpeakerLevel) : 0;
            MicLevelPercent = IsMicCaptureEnabled ? ConvertLevelToPercent(st.MicLevel) : 0;
        });
    }
    private void OnTranscriptionJobCompleted(object? sender, TranscriptionJobCompletedEventArgs e)
    {
        if (e.Request.Trigger != TranscriptionTrigger.AutoAfterRecord)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (e.Result.Succeeded)
            {
                if (e.Request.Options.TranscriptionToastNotificationEnabled)
                {
                    AppNotificationHub.Notify("VoxArchive", $"自動文字起こし完了: {Path.GetFileName(e.Request.AudioFilePath)}", System.Windows.Forms.ToolTipIcon.Info);
                }

                return;
            }

            _logger.LogWarning("文字起こし失敗: {Message}", e.Result.Message);
            if (e.Request.Options.TranscriptionToastNotificationEnabled)
            {
                AppNotificationHub.Notify("VoxArchive", $"自動文字起こし失敗: {e.Result.Message}", System.Windows.Forms.ToolTipIcon.Warning);
            }
        });
    }
    private void RefreshCommands()
    {
        StartStopCommand.RaiseCanExecuteChanged();
        PauseResumeCommand.RaiseCanExecuteChanged();
        ToggleOutputModeCommand.RaiseCanExecuteChanged();
        ToggleWindowModeCommand.RaiseCanExecuteChanged();
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
            StartStopHotkey = normalizedHotkey,
            DefaultSpeakerPlaybackGainDb = Math.Clamp(options.DefaultSpeakerPlaybackGainDb, -60d, 48d),
            DefaultMicPlaybackGainDb = Math.Clamp(options.DefaultMicPlaybackGainDb, -60d, 48d),
            TranscriptionLanguage = string.IsNullOrWhiteSpace(options.TranscriptionLanguage) ? "ja" : options.TranscriptionLanguage.Trim(),
            TranscriptionOutputFormats = options.TranscriptionOutputFormats == TranscriptionOutputFormats.None
                ? TranscriptionOutputFormats.Txt
                : options.TranscriptionOutputFormats
        };
    }


    private static string BuildFfmpegMissingMessage(string detail)
    {
        var baseMessage =
            "ffmpeg が見つかりません。録音を開始できません。" + Environment.NewLine +
            "ffmpeg をインストールして PATH を通した後に再試行してください。" + Environment.NewLine +
            Environment.NewLine +
            "インストール例: winget install Gyan.FFmpeg";

        if (string.IsNullOrWhiteSpace(detail))
        {
            return baseMessage;
        }

        return baseMessage + Environment.NewLine + Environment.NewLine + "詳細: " + detail;
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

    private static Brush BuildRingBrush(bool isEnabled, double levelPercent, Color accent)
    {
        if (!isEnabled)
        {
            return new SolidColorBrush(Color.FromRgb(74, 86, 104));
        }

        var t = Math.Clamp(levelPercent / 100d, 0d, 1d);
        var baseColor = Color.FromRgb(49, 64, 85);
        var ring = InterpolateColor(baseColor, accent, t);
        return new SolidColorBrush(ring);
    }

    private static Color InterpolateColor(Color from, Color to, double t)
    {
        var r = (byte)(from.R + ((to.R - from.R) * t));
        var g = (byte)(from.G + ((to.G - from.G) * t));
        var b = (byte)(from.B + ((to.B - from.B) * t));
        return Color.FromRgb(r, g, b);
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

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
    }

    private void EnsureSpeakerDevicePopupState()
    {
        if (IsSpeakerDeviceSelectionEnabled)
        {
            return;
        }

        IsSpeakerDevicePopupOpenNormal = false;
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

    public void Dispose()
    {
        _recordingService.StateChanged -= OnRecordingStateChanged;
        _recordingService.ErrorOccurred -= OnRecordingErrorOccurred;
        _recordingService.OutputSourceChanged -= OnOutputSourceChanged;
        _recordingService.StatisticsUpdated -= OnStatisticsUpdated;
        _transcriptionQueue.JobCompleted -= OnTranscriptionJobCompleted;
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

