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
    private RecordingOptions _options;

    private string _stateText = "状態: Stopped";
    private string _outputPathText = "出力: -";
    private string _metricsText = "統計: -";
    private string _lastErrorText = string.Empty;

    public MainViewModel(RecordingRuntimeContext context)
    {
        _recordingService = context.RecordingService;
        _settingsService = context.SettingsService;
        _options = EnsureDefaults(context.DefaultOptions);

        StartCommand = new DelegateCommand(StartAsync, () => _recordingService.CurrentState is RecordingState.Stopped or RecordingState.Error);
        PauseCommand = new DelegateCommand(PauseAsync, () => _recordingService.CurrentState == RecordingState.Recording);
        ResumeCommand = new DelegateCommand(ResumeAsync, () => _recordingService.CurrentState == RecordingState.Paused);
        StopCommand = new DelegateCommand(StopAsync, () => _recordingService.CurrentState is RecordingState.Recording or RecordingState.Paused);

        _recordingService.StateChanged += (_, s) => RunOnUi(() =>
        {
            StateText = $"状態: {s}";
            RefreshCommands();
        });

        _recordingService.ErrorOccurred += (_, e) => RunOnUi(() => LastErrorText = $"エラー: {e}");
        _recordingService.OutputSourceChanged += (_, e) => RunOnUi(() => LastErrorText = $"出力切替: {e.Previous} -> {e.Current} ({e.Reason})");
        _recordingService.StatisticsUpdated += (_, st) => RunOnUi(() =>
        {
            OutputPathText = $"出力: {st.OutputFilePath ?? "-"}";
            MetricsText = $"経過 {st.ElapsedTime:hh\\:mm\\:ss} / Drift {st.DriftCorrectionPpm:F1} ppm / MicBuf {st.MicBufferMilliseconds:F0}ms / SpkBuf {st.SpeakerBufferMilliseconds:F0}ms";
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DelegateCommand StartCommand { get; }
    public DelegateCommand PauseCommand { get; }
    public DelegateCommand ResumeCommand { get; }
    public DelegateCommand StopCommand { get; }

    public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }
    public string OutputPathText { get => _outputPathText; private set => SetField(ref _outputPathText, value); }
    public string MetricsText { get => _metricsText; private set => SetField(ref _metricsText, value); }
    public string LastErrorText { get => _lastErrorText; private set => SetField(ref _lastErrorText, value); }

    private async Task StartAsync()
    {
        LastErrorText = string.Empty;
        _options = EnsureDefaults(_options);
        await _settingsService.SaveRecordingOptionsAsync(_options);
        var path = await _recordingService.StartAsync(_options);
        OutputPathText = $"出力: {path}";
    }

    private async Task PauseAsync()
    {
        await _recordingService.PauseAsync();
    }

    private async Task ResumeAsync()
    {
        await _recordingService.ResumeAsync();
    }

    private async Task StopAsync()
    {
        await _recordingService.StopAsync();
    }

    private void RefreshCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
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
            OutputDirectory = output,
            SpeakerDeviceId = string.IsNullOrWhiteSpace(options.SpeakerDeviceId) ? "default-speaker" : options.SpeakerDeviceId,
            MicDeviceId = string.IsNullOrWhiteSpace(options.MicDeviceId) ? "default-mic" : options.MicDeviceId,
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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
