using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using ApplicationTranscriptionJobCompletedEventArgs = VoxArchive.Application.Abstractions.TranscriptionJobCompletedEventArgs;
using ApplicationTranscriptionJobStateChangedEventArgs = VoxArchive.Application.Abstractions.TranscriptionJobStateChangedEventArgs;
using ApplicationTranscriptionTrigger = VoxArchive.Application.Abstractions.TranscriptionTrigger;

namespace VoxArchive.Wpf;

public sealed class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan PositionUpdateInterval = TimeSpan.FromMilliseconds(33);
    private readonly RecordingCatalogService _catalogService;
    private readonly IDisposable _catalogSession;
    private readonly IRecordingPlaybackService _playbackService;
    private readonly DispatcherTimer _positionTimer;
    private readonly ITranscriptionApplicationService _transcriptionService;
    private readonly Func<RecordingOptions> _optionsProvider;

    private LibraryRecordingItem? _selectedItem;
    private string _editableTitle = string.Empty;
    private string _editableFileName = string.Empty;
    private bool _isPlaying;
    private string _playbackButtonText = "再生";
    private double _speakerGainDb;
    private double _micGainDb;
    private double _seekSeconds;
    private double _durationSeconds;
    private string _positionText = "00:00 / 00:00";
    private string _statusText = "準備完了";
    private readonly object _transcribingFilesGate = new();
    private readonly HashSet<string> _transcribingFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _mixToMonoPlayback = true;
    private bool _isSeekingByUser;
    private bool _isUpdatingFromPlayer;
    private SeekStepOption? _selectedSeekStepOption = new(10, "10秒");
    private PlaybackSpeedOption? _selectedPlaybackSpeedOption = new(1.0, "1.0x");
    private bool _allItemsChecked;
    private bool _isSavingMonoMix;

    public LibraryViewModel(
        RecordingCatalogService catalogService,
        ITranscriptionApplicationService transcriptionService,
        Func<RecordingOptions> optionsProvider,
        IRecordingPlaybackService playbackService,
        double defaultSpeakerGainDb = 0d,
        double defaultMicGainDb = 0d)
    {
        _catalogService = catalogService;
        _catalogSession = _catalogService.AcquireInteractiveSession();
        _transcriptionService = transcriptionService;
        _optionsProvider = optionsProvider;
        _transcriptionService.JobCompleted += OnTranscriptionJobCompleted;
        _transcriptionService.JobStateChanged += OnTranscriptionJobStateChanged;

        foreach (var snapshot in _transcriptionService.GetJobStates())
        {
            lock (_transcribingFilesGate) _transcribingFiles.Add(NormalizePathKey(snapshot.AudioFilePath));
        }

        _playbackService = playbackService;
        _playbackService.PlaybackStopped += OnPlaybackStopped;
        _speakerGainDb = defaultSpeakerGainDb;
        _micGainDb = defaultMicGainDb;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PositionUpdateInterval };
        _positionTimer.Tick += (_, _) => UpdatePositionFromPlayer();

        Items = new ObservableCollection<LibraryRecordingItem>();
        SeekStepOptions = [new(5, "5秒"), new(10, "10秒"), new(15, "15秒"), new(30, "30秒"), new(60, "1分"), new(300, "5分"), new(600, "10分")];
        SelectedSeekStepOption = SeekStepOptions[1];
        PlaybackSpeedOptions = [new(0.5, "0.5x"), new(0.8, "0.8x"), new(1.0, "1.0x"), new(1.2, "1.2x"), new(1.5, "1.5x"), new(2.0, "2.0x"), new(2.5, "2.5x"), new(3.0, "3.0x"), new(4.0, "4.0x")];
        SelectedPlaybackSpeedOption = PlaybackSpeedOptions.First(x => Math.Abs(x.Rate - 1.0) < 0.0001);

        RefreshCommand = new DelegateCommand(RefreshAsync);
        AddFileCommand = new DelegateCommand(AddFileAsync);
        RemoveMissingFromListCommand = new DelegateCommand(RemoveMissingFromListAsync);
        TogglePlaybackCommand = new DelegateCommand(TogglePlaybackAsync, () => SelectedItem is not null);
        StopCommand = new DelegateCommand(StopAsync, () => _playbackService.IsLoaded);
        SaveTitleCommand = new DelegateCommand(SaveTitleAsync, CanSaveTitle);
        RenameCommand = new DelegateCommand(RenameAsync, CanRename);
        DeleteFileCommand = new DelegateCommand(DeleteFileAsync, () => SelectedItem is not null);
        RemoveFromListCommand = new DelegateCommand(RemoveFromListAsync, () => SelectedItem is not null);
        RemoveCheckedFromListCommand = new DelegateCommand(RemoveCheckedFromListAsync, CanRemoveCheckedFromList);
        DeleteCheckedFilesCommand = new DelegateCommand(DeleteCheckedFilesAsync, CanDeleteCheckedFiles);
        OpenInExplorerCommand = new DelegateCommand(OpenInExplorerAsync, () => SelectedItem is not null);
        OpenTranscriptionFileCommand = new DelegateCommand(OpenTranscriptionFileAsync, CanOpenTranscriptionFile);
        TranscribeCommand = new DelegateCommand(TranscribeAsync, CanTranscribe);
        SeekBackwardCommand = new DelegateCommand(SeekBackwardAsync, () => SelectedItem is not null);
        SeekForwardCommand = new DelegateCommand(SeekForwardAsync, () => SelectedItem is not null);
        SaveMonoMixCommand = new DelegateCommand(SaveMonoMixAsync, CanSaveMonoMix);
        ResetPlaybackSpeedCommand = new DelegateCommand(ResetPlaybackSpeedAsync, CanResetPlaybackSpeed);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<LibraryRecordingItem> Items { get; }
    public IReadOnlyList<SeekStepOption> SeekStepOptions { get; }
    public IReadOnlyList<PlaybackSpeedOption> PlaybackSpeedOptions { get; }
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand AddFileCommand { get; }
    public DelegateCommand RemoveMissingFromListCommand { get; }
    public DelegateCommand TogglePlaybackCommand { get; }
    public DelegateCommand StopCommand { get; }
    public DelegateCommand SaveTitleCommand { get; }
    public DelegateCommand RenameCommand { get; }
    public DelegateCommand DeleteFileCommand { get; }
    public DelegateCommand RemoveFromListCommand { get; }
    public DelegateCommand RemoveCheckedFromListCommand { get; }
    public DelegateCommand DeleteCheckedFilesCommand { get; }
    public DelegateCommand OpenInExplorerCommand { get; }
    public DelegateCommand OpenTranscriptionFileCommand { get; }
    public DelegateCommand TranscribeCommand { get; }
    public DelegateCommand SeekBackwardCommand { get; }
    public DelegateCommand SeekForwardCommand { get; }
    public DelegateCommand SaveMonoMixCommand { get; }
    public DelegateCommand ResetPlaybackSpeedCommand { get; }

    public LibraryRecordingItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value)) return;
            EditableTitle = value?.Title ?? string.Empty;
            EditableFileName = value?.FileName ?? string.Empty;
            LoadSelectedForPlayback();
            OnPropertyChanged(nameof(IsTranscribing));
            RaiseCommands();
        }
    }
    public string EditableTitle { get => _editableTitle; set { if (SetField(ref _editableTitle, value)) RaiseCommands(); } }
    public string EditableFileName { get => _editableFileName; set { if (SetField(ref _editableFileName, value)) RaiseCommands(); } }
    public bool IsPlaying { get => _isPlaying; private set => SetField(ref _isPlaying, value); }
    public string PlaybackButtonText { get => _playbackButtonText; private set => SetField(ref _playbackButtonText, value); }
    public double SpeakerGainDb { get => _speakerGainDb; set { if (SetField(ref _speakerGainDb, value)) _playbackService.SetGains(_speakerGainDb, _micGainDb); } }
    public double MicGainDb { get => _micGainDb; set { if (SetField(ref _micGainDb, value)) _playbackService.SetGains(_speakerGainDb, _micGainDb); } }
    public double SeekSeconds
    {
        get => _seekSeconds;
        set
        {
            if (!SetField(ref _seekSeconds, value)) return;
            if (!_isUpdatingFromPlayer) _playbackService.Seek(TimeSpan.FromSeconds(value));
            UpdatePositionText();
        }
    }
    public double DurationSeconds { get => _durationSeconds; private set => SetField(ref _durationSeconds, value); }
    public string PositionText { get => _positionText; private set => SetField(ref _positionText, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public bool AllItemsChecked
    {
        get => _allItemsChecked;
        set
        {
            if (!SetField(ref _allItemsChecked, value)) return;
            foreach (var item in Items) item.IsChecked = value;
            RaiseCommands();
        }
    }
    public SeekStepOption? SelectedSeekStepOption { get => _selectedSeekStepOption; set => SetField(ref _selectedSeekStepOption, value); }
    public PlaybackSpeedOption? SelectedPlaybackSpeedOption
    {
        get => _selectedPlaybackSpeedOption;
        set
        {
            if (!SetField(ref _selectedPlaybackSpeedOption, value)) return;
            _playbackService.SetPlaybackSpeed(value?.Rate ?? 1.0);
            RaiseCommands();
        }
    }
    public bool MixToMonoPlayback { get => _mixToMonoPlayback; set { if (SetField(ref _mixToMonoPlayback, value)) _playbackService.SetMixToMono(value); } }
    public bool IsTranscribing => IsTranscribingForPath(SelectedItem?.FilePath);

    public Task InitializeAsync() => RefreshAsync();
    public void BeginSeek() => _isSeekingByUser = true;
    public void EndSeek()
    {
        _isSeekingByUser = false;
        if (!_playbackService.IsLoaded) return;
        _playbackService.Seek(TimeSpan.FromSeconds(SeekSeconds));
        UpdatePositionText();
    }

    public async Task ReloadAsync(string? newFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(newFilePath))
        {
            try { await _catalogService.AddOrUpdateFileAsync(newFilePath); }
            catch (Exception ex) { StatusText = $"追加失敗: {ex.Message}"; }
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            foreach (var item in Items.ToList()) item.PropertyChanged -= OnItemPropertyChanged;
            var list = await _catalogService.GetAllAsync();
            Items.Clear();
            foreach (var item in list) { Items.Add(item); item.PropertyChanged += OnItemPropertyChanged; }
            if (SelectedItem is not null) SelectedItem = Items.FirstOrDefault(x => x.FilePath == SelectedItem.FilePath);
            UpdateAllItemsCheckedState();
            StatusText = $"{Items.Count} 件";
        }
        catch (Exception ex) { StatusText = $"読み込み失敗: {ex.Message}"; }
    }

    private async Task AddFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "ライブラリへ追加するFLACを選択", Filter = "FLAC (*.flac)|*.flac", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        var added = 0;
        foreach (var file in dialog.FileNames)
        {
            try { await _catalogService.AddOrUpdateFileAsync(file); added++; }
            catch (Exception ex) { StatusText = $"追加失敗: {ex.Message}"; }
        }
        await RefreshAsync();
        if (added > 0) StatusText = $"{added} 件追加しました。";
    }

    private void LoadSelectedForPlayback()
    {
        StopPlaybackState();
        if (SelectedItem is null || !File.Exists(SelectedItem.FilePath)) { DurationSeconds = 0; SeekSeconds = 0; PositionText = "00:00 / 00:00"; return; }
        try
        {
            _playbackService.Load(SelectedItem.FilePath);
            _playbackService.SetGains(SpeakerGainDb, MicGainDb);
            _playbackService.SetMixToMono(MixToMonoPlayback);
            _playbackService.SetPlaybackSpeed(SelectedPlaybackSpeedOption?.Rate ?? 1.0);
            DurationSeconds = Math.Max(0, _playbackService.Duration.TotalSeconds);
            SeekSeconds = 0;
            UpdatePositionText();
        }
        catch (Exception ex) { StatusText = $"再生準備失敗: {ex.Message}"; }
        RaiseCommands();
    }

    private async Task TogglePlaybackAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("再生", SelectedItem.FilePath)) return;
        if (!_playbackService.IsLoaded) LoadSelectedForPlayback();
        if (!_playbackService.IsLoaded) return;
        if (_playbackService.IsPlaying) { _playbackService.Pause(); _positionTimer.Stop(); IsPlaying = false; PlaybackButtonText = "再生"; }
        else { _playbackService.Play(); _positionTimer.Start(); IsPlaying = true; PlaybackButtonText = "一時停止"; }
        RaiseCommands();
    }

    private Task StopAsync() { _playbackService.Stop(); StopPlaybackState(); SeekSeconds = 0; UpdatePositionText(); return Task.CompletedTask; }

    private async Task SaveTitleAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("タイトル保存", SelectedItem.FilePath)) return;
        var title = EditableTitle.Trim();
        if (string.IsNullOrWhiteSpace(title)) { StatusText = "タイトルが空です。"; return; }
        try { await _catalogService.UpdateTitleAsync(SelectedItem.FilePath, title); await RefreshAsync(); StatusText = "タイトルを更新しました。"; }
        catch (Exception ex) { StatusText = $"タイトル更新失敗: {ex.Message}"; }
    }

    private bool CanSaveTitle() => SelectedItem is not null && !string.Equals(EditableTitle.Trim(), (SelectedItem.Title ?? string.Empty).Trim(), StringComparison.Ordinal);

    private async Task RenameAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("リネーム", SelectedItem.FilePath)) return;
        try { var newPath = await _catalogService.RenameAsync(SelectedItem.FilePath, EditableFileName); await RefreshAsync(); SelectedItem = Items.FirstOrDefault(x => x.FilePath == newPath); StatusText = "ファイル名を変更しました。"; }
        catch (IOException ex) { ModernDialog.Show($"同名のファイルが既に存在するため、リネームできません。\n{ex.Message}", "リネーム失敗", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK); StatusText = $"リネーム失敗: {ex.Message}"; }
        catch (Exception ex) { ModernDialog.Show(ex.Message, "リネーム失敗", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK); StatusText = $"リネーム失敗: {ex.Message}"; }
    }

    private bool CanRename() => SelectedItem is not null && !string.Equals(EditableFileName.Trim(), (SelectedItem.FileName ?? string.Empty).Trim(), StringComparison.Ordinal);

    private async Task DeleteFileAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("ファイル削除", SelectedItem.FilePath)) return;
        if (ModernDialog.Show($"ファイルを削除します。\n{SelectedItem.FileName}", "削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { await _catalogService.DeleteFileAsync(SelectedItem.FilePath); await RefreshAsync(); StatusText = "ファイルを削除しました。"; }
        catch (Exception ex) { StatusText = $"削除失敗: {ex.Message}"; }
    }

    private async Task RemoveMissingFromListAsync()
    {
        var missing = Items.Where(x => !File.Exists(x.FilePath)).ToList();
        if (missing.Count == 0) { StatusText = "削除対象の欠損ファイル情報はありません。"; return; }
        if (ModernDialog.Show($"実ファイルが見つからない {missing.Count} 件を一覧から削除します。", "一括削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { foreach (var item in missing) await _catalogService.RemoveFromListAsync(item.FilePath); await RefreshAsync(); StatusText = $"{missing.Count} 件を一覧から削除しました。"; }
        catch (Exception ex) { StatusText = $"一括削除失敗: {ex.Message}"; }
    }

    private async Task OpenInExplorerAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("Explorer表示", SelectedItem.FilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedItem.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText = $"Explorer起動失敗: {ex.Message}"; }
    }

    private Task OpenTranscriptionFileAsync()
    {
        if (SelectedItem is null) return Task.CompletedTask;
        var outputPath = _transcriptionService.FindCanonicalResultPath(SelectedItem.FilePath, _optionsProvider());
        if (outputPath is null) { StatusText = "現在設定に対応する文字起こしファイルが見つかりません。"; return Task.CompletedTask; }
        try { Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true }); StatusText = $"文字起こしファイルを開きました: {Path.GetFileName(outputPath)}"; }
        catch (Exception ex) { StatusText = $"文字起こしファイルを開けませんでした: {ex.Message}"; }
        return Task.CompletedTask;
    }

    private bool CanOpenTranscriptionFile() => SelectedItem is not null && _transcriptionService.FindCanonicalResultPath(SelectedItem.FilePath, _optionsProvider()) is not null;

    private bool CanTranscribe()
    {
        if (SelectedItem is null) return false;
        var options = _optionsProvider();
        return !IsTranscribingForPath(SelectedItem.FilePath)
            && options.Transcription.Enabled
            && _transcriptionService.FindCanonicalResultPath(SelectedItem.FilePath, options) is null;
    }

    public void NotifyOptionsChanged() { TranscribeCommand.RaiseCanExecuteChanged(); OpenTranscriptionFileCommand.RaiseCanExecuteChanged(); }

    private async Task TranscribeAsync()
    {
        try
        {
            if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("文字起こし", SelectedItem.FilePath)) return;
            var options = _optionsProvider();
            if (!options.Transcription.Enabled) { StatusText = "文字起こし機能が無効です。設定画面で有効化してください。"; return; }

            var result = await _transcriptionService.TryEnqueueAsync(SelectedItem.FilePath, options, ApplicationTranscriptionTrigger.Manual);
            if (!result.Enqueued)
            {
                StatusText = IsTranscribingForPath(SelectedItem.FilePath) ? "このファイルは既に文字起こしキューに投入済みです。" : result.Message;
                return;
            }

            OnPropertyChanged(nameof(IsTranscribing));
            RaiseCommands();
            if (options.Transcription.ToastNotificationEnabled) AppNotificationHub.Notify("VoxArchive", $"文字起こし開始: {Path.GetFileName(SelectedItem.FilePath)}", System.Windows.Forms.ToolTipIcon.Info);
            StatusText = "文字起こしジョブをキューへ追加しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"文字起こし開始時に例外が発生しました: {ex.Message}";
            ModernDialog.Show($"文字起こし開始時に例外が発生しました。\n{ex.Message}", "文字起こしエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTranscriptionJobCompleted(object? sender, ApplicationTranscriptionJobCompletedEventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        _ = app.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(IsTranscribing)); RaiseCommands(); });
    }

    private void OnTranscriptionJobStateChanged(object? sender, ApplicationTranscriptionJobStateChangedEventArgs e)
    {
        var key = NormalizePathKey(e.AudioFilePath);
        lock (_transcribingFilesGate)
        {
            if (e.State is null) _transcribingFiles.Remove(key);
            else _transcribingFiles.Add(key);
        }
        var app = System.Windows.Application.Current;
        if (app is null) return;
        _ = app.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(IsTranscribing)); RaiseCommands(); });
    }

    private bool IsTranscribingForPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        lock (_transcribingFilesGate) return _transcribingFiles.Contains(NormalizePathKey(filePath));
    }

    private static string NormalizePathKey(string path) { try { return Path.GetFullPath(path).Trim(); } catch { return path.Trim(); } }
    private Task SeekBackwardAsync() => SeekRelativeAsync(-1);
    private Task SeekForwardAsync() => SeekRelativeAsync(1);

    private async Task SeekRelativeAsync(int direction)
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("シーク", SelectedItem.FilePath)) return;
        if (!_playbackService.IsLoaded) LoadSelectedForPlayback();
        if (!_playbackService.IsLoaded) return;
        SeekSeconds = Math.Clamp(SeekSeconds + (direction * (SelectedSeekStepOption?.Seconds ?? 10)), 0d, DurationSeconds);
        UpdatePositionText();
    }

    private async Task RemoveFromListAsync()
    {
        if (SelectedItem is null) return;
        try { await _catalogService.RemoveFromListAsync(SelectedItem.FilePath); await RefreshAsync(); StatusText = "一覧から削除しました（ファイルは残ります）。"; }
        catch (Exception ex) { StatusText = $"一覧削除失敗: {ex.Message}"; }
    }

    private bool CanResetPlaybackSpeed() => Math.Abs((SelectedPlaybackSpeedOption?.Rate ?? 1.0) - 1.0) > 0.0001;
    private Task ResetPlaybackSpeedAsync()
    {
        var normal = PlaybackSpeedOptions.FirstOrDefault(x => Math.Abs(x.Rate - 1.0) < 0.0001);
        if (normal is not null) SelectedPlaybackSpeedOption = normal;
        return Task.CompletedTask;
    }

    private bool CanSaveMonoMix() => SelectedItem is not null && !_isSavingMonoMix;

    private async Task SaveMonoMixAsync()
    {
        if (SelectedItem is null || !await EnsureFileExistsOrPromptRemoveAsync("モノラル保存", SelectedItem.FilePath)) return;
        var inputPath = SelectedItem.FilePath;
        var format = MonoMixdownOutputFormat.Wav;
        var dialog = new SaveFileDialog
        {
            Title = "モノラル変換ファイルの保存先",
            Filter = "WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|FLAC (*.flac)|*.flac",
            FilterIndex = 1,
            FileName = BuildDefaultMonoMixFileName(inputPath, format),
            InitialDirectory = Path.GetDirectoryName(inputPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        format = dialog.FilterIndex switch { 2 => MonoMixdownOutputFormat.Mp3, 3 => MonoMixdownOutputFormat.Flac, _ => MonoMixdownOutputFormat.Wav };
        var outputPath = EnsureOutputExtension(dialog.FileName, format);
        try
        {
            _isSavingMonoMix = true;
            RaiseCommands();
            StatusText = "モノラル変換ファイルを書き出し中...";
            await MonoMixdownExportService.ExportAsync(inputPath, outputPath, SpeakerGainDb, MicGainDb, format, _optionsProvider().FfmpegExecutablePath);
            StatusText = $"モノラル変換ファイルを保存しました: {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex) { StatusText = $"モノラル保存失敗: {ex.Message}"; }
        finally { _isSavingMonoMix = false; RaiseCommands(); }
    }

    private async Task<bool> EnsureFileExistsOrPromptRemoveAsync(string actionName, string filePath)
    {
        if (File.Exists(filePath)) return true;
        await HandleMissingFileAsync(actionName, filePath);
        return false;
    }

    private async Task HandleMissingFileAsync(string actionName, string filePath)
    {
        if (ModernDialog.Show($"{actionName}の対象ファイルが見つかりません。\n{filePath}\n\n一覧から削除しますか？", "ファイル未検出", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            StatusText = "ファイルが見つかりません。";
            return;
        }
        try { await _catalogService.RemoveFromListAsync(filePath); await RefreshAsync(); StatusText = "一覧から削除しました（ファイルは残ります）。"; }
        catch (Exception ex) { StatusText = $"一覧削除失敗: {ex.Message}"; }
    }

    private List<LibraryRecordingItem> GetCheckedItems() => Items.Where(x => x.IsChecked).ToList();
    private bool CanRemoveCheckedFromList() => Items.Any(x => x.IsChecked);
    private bool CanDeleteCheckedFiles() => Items.Any(x => x.IsChecked);

    private async Task RemoveCheckedFromListAsync()
    {
        var checkedItems = GetCheckedItems();
        if (checkedItems.Count == 0 || ModernDialog.Show($"チェックした {checkedItems.Count} 件を一覧から削除します（ファイルは残ります）。", "一括削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { foreach (var item in checkedItems) await _catalogService.RemoveFromListAsync(item.FilePath); await RefreshAsync(); StatusText = $"{checkedItems.Count} 件を一覧から削除しました（ファイルは残ります）。"; }
        catch (Exception ex) { StatusText = $"一覧一括削除失敗: {ex.Message}"; }
    }

    private async Task DeleteCheckedFilesAsync()
    {
        var checkedItems = GetCheckedItems();
        if (checkedItems.Count == 0 || ModernDialog.Show($"チェックした {checkedItems.Count} 件のファイルを削除します。", "一括ファイル削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { foreach (var item in checkedItems) await _catalogService.DeleteFileAsync(item.FilePath); await RefreshAsync(); StatusText = $"{checkedItems.Count} 件のファイルを削除しました。"; }
        catch (Exception ex) { StatusText = $"ファイル一括削除失敗: {ex.Message}"; }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryRecordingItem.IsChecked)) return;
        UpdateAllItemsCheckedState();
        RaiseCommands();
    }

    private void UpdateAllItemsCheckedState()
    {
        var allChecked = Items.Count > 0 && Items.All(x => x.IsChecked);
        if (_allItemsChecked == allChecked) return;
        _allItemsChecked = allChecked;
        OnPropertyChanged(nameof(AllItemsChecked));
    }

    private void OnPlaybackStopped(object? sender, EventArgs e) { IsPlaying = false; PlaybackButtonText = "再生"; }
    private void UpdatePositionFromPlayer()
    {
        if (_isSeekingByUser) return;
        _isUpdatingFromPlayer = true;
        try { SeekSeconds = _playbackService.Position.TotalSeconds; }
        finally { _isUpdatingFromPlayer = false; }
    }
    private void UpdatePositionText()
    {
        var pos = TimeSpan.FromSeconds(SeekSeconds);
        var dur = TimeSpan.FromSeconds(DurationSeconds);
        var format = dur.TotalHours >= 1d ? @"hh\:mm\:ss" : @"mm\:ss";
        PositionText = $"{pos.ToString(format)} / {dur.ToString(format)}";
    }
    private void StopPlaybackState() { _positionTimer.Stop(); IsPlaying = false; PlaybackButtonText = "再生"; }

    private void RaiseCommands()
    {
        TogglePlaybackCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); SaveTitleCommand.RaiseCanExecuteChanged(); RenameCommand.RaiseCanExecuteChanged();
        DeleteFileCommand.RaiseCanExecuteChanged(); RemoveFromListCommand.RaiseCanExecuteChanged(); RemoveCheckedFromListCommand.RaiseCanExecuteChanged(); DeleteCheckedFilesCommand.RaiseCanExecuteChanged();
        OpenInExplorerCommand.RaiseCanExecuteChanged(); OpenTranscriptionFileCommand.RaiseCanExecuteChanged(); TranscribeCommand.RaiseCanExecuteChanged(); SeekBackwardCommand.RaiseCanExecuteChanged(); SeekForwardCommand.RaiseCanExecuteChanged(); SaveMonoMixCommand.RaiseCanExecuteChanged(); ResetPlaybackSpeedCommand.RaiseCanExecuteChanged();
    }

    private static string BuildDefaultMonoMixFileName(string sourcePath, MonoMixdownOutputFormat format)
    {
        var extension = format switch { MonoMixdownOutputFormat.Mp3 => ".mp3", MonoMixdownOutputFormat.Flac => ".flac", _ => ".wav" };
        return $"{Path.GetFileNameWithoutExtension(sourcePath)}-mono{extension}";
    }
    private static string EnsureOutputExtension(string path, MonoMixdownOutputFormat format)
    {
        var wanted = format switch { MonoMixdownOutputFormat.Mp3 => ".mp3", MonoMixdownOutputFormat.Flac => ".flac", _ => ".wav" };
        return string.Equals(Path.GetExtension(path), wanted, StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, wanted);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed record SeekStepOption(int Seconds, string Label);
    public sealed record PlaybackSpeedOption(double Rate, string Label);

    public void Dispose()
    {
        _transcriptionService.JobCompleted -= OnTranscriptionJobCompleted;
        _transcriptionService.JobStateChanged -= OnTranscriptionJobStateChanged;
        foreach (var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
        _positionTimer.Stop();
        _playbackService.PlaybackStopped -= OnPlaybackStopped;
        _playbackService.Dispose();
        _catalogSession.Dispose();
    }
}
