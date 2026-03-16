using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan PositionUpdateInterval = TimeSpan.FromMilliseconds(33);

    private readonly RecordingCatalogService _catalogService;
    private readonly RecordingPlaybackService _playbackService;
    private readonly DispatcherTimer _positionTimer;
    private readonly TranscriptionJobQueue _transcriptionQueue;
    private readonly Func<RecordingOptions> _optionsProvider;

    private const TranscriptionOutputFormats AllTranscriptionOutputFormats = TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt | TranscriptionOutputFormats.Vtt | TranscriptionOutputFormats.Json;

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
    private bool _mixToMonoPlayback = true;
    private bool _isTranscribing;
    private bool _isSeekingByUser;
    private bool _isUpdatingFromPlayer;
    private SeekStepOption? _selectedSeekStepOption = new(10, "10秒");
    private bool _allItemsChecked;

    public LibraryViewModel(
        RecordingCatalogService catalogService,
        TranscriptionJobQueue transcriptionQueue,
        Func<RecordingOptions> optionsProvider,
        double defaultSpeakerGainDb = 0d,
        double defaultMicGainDb = 0d)
    {
        _catalogService = catalogService;
        _transcriptionQueue = transcriptionQueue;
        _optionsProvider = optionsProvider;

        _transcriptionQueue.JobCompleted += OnTranscriptionJobCompleted;

        _playbackService = new RecordingPlaybackService();
        _playbackService.PlaybackStopped += (_, _) =>
        {
            IsPlaying = false;
            PlaybackButtonText = "再生";
        };

        _speakerGainDb = defaultSpeakerGainDb;
        _micGainDb = defaultMicGainDb;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = PositionUpdateInterval
        };
        _positionTimer.Tick += (_, _) => UpdatePositionFromPlayer();

        Items = new ObservableCollection<LibraryRecordingItem>();
        SeekStepOptions = new[]
        {
            new SeekStepOption(5, "5秒"),
            new SeekStepOption(10, "10秒"),
            new SeekStepOption(15, "15秒"),
            new SeekStepOption(30, "30秒"),
            new SeekStepOption(60, "1分"),
            new SeekStepOption(300, "5分"),
            new SeekStepOption(600, "10分")
        };
        SelectedSeekStepOption = SeekStepOptions[1];

        RefreshCommand = new DelegateCommand(RefreshAsync);
        AddFileCommand = new DelegateCommand(AddFileAsync);
        RemoveMissingFromListCommand = new DelegateCommand(RemoveMissingFromListAsync);
        TogglePlaybackCommand = new DelegateCommand(TogglePlaybackAsync, () => SelectedItem is not null);
        StopCommand = new DelegateCommand(StopAsync, () => _playbackService.IsLoaded);
        SaveTitleCommand = new DelegateCommand(SaveTitleAsync, () => SelectedItem is not null);
        RenameCommand = new DelegateCommand(RenameAsync, () => SelectedItem is not null);
        DeleteFileCommand = new DelegateCommand(DeleteFileAsync, () => SelectedItem is not null);
        RemoveFromListCommand = new DelegateCommand(RemoveFromListAsync, () => SelectedItem is not null);
        RemoveCheckedFromListCommand = new DelegateCommand(RemoveCheckedFromListAsync, CanRemoveCheckedFromList);
        DeleteCheckedFilesCommand = new DelegateCommand(DeleteCheckedFilesAsync, CanDeleteCheckedFiles);
        OpenInExplorerCommand = new DelegateCommand(OpenInExplorerAsync, () => SelectedItem is not null);
        OpenTranscriptionFileCommand = new DelegateCommand(OpenTranscriptionFileAsync, CanOpenTranscriptionFile);
        TranscribeCommand = new DelegateCommand(TranscribeAsync, CanTranscribe);
        SeekBackwardCommand = new DelegateCommand(SeekBackwardAsync, () => SelectedItem is not null);
        SeekForwardCommand = new DelegateCommand(SeekForwardAsync, () => SelectedItem is not null);

        _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LibraryRecordingItem> Items { get; }
    public IReadOnlyList<SeekStepOption> SeekStepOptions { get; }

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

    public LibraryRecordingItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value))
            {
                return;
            }

            EditableTitle = value?.Title ?? string.Empty;
            EditableFileName = value?.FileName ?? string.Empty;
            LoadSelectedForPlayback();
            RaiseCommands();
        }
    }

    public string EditableTitle { get => _editableTitle; set => SetField(ref _editableTitle, value); }
    public string EditableFileName { get => _editableFileName; set => SetField(ref _editableFileName, value); }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    public string PlaybackButtonText
    {
        get => _playbackButtonText;
        private set => SetField(ref _playbackButtonText, value);
    }

    public double SpeakerGainDb
    {
        get => _speakerGainDb;
        set
        {
            if (SetField(ref _speakerGainDb, value))
            {
                _playbackService.SetGains(_speakerGainDb, _micGainDb);
            }
        }
    }

    public double MicGainDb
    {
        get => _micGainDb;
        set
        {
            if (SetField(ref _micGainDb, value))
            {
                _playbackService.SetGains(_speakerGainDb, _micGainDb);
            }
        }
    }

    public double SeekSeconds
    {
        get => _seekSeconds;
        set
        {
            if (!SetField(ref _seekSeconds, value))
            {
                return;
            }

            if (!_isUpdatingFromPlayer)
            {
                _playbackService.Seek(TimeSpan.FromSeconds(value));
            }

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
            if (!SetField(ref _allItemsChecked, value))
            {
                return;
            }

            foreach (var item in Items)
            {
                item.IsChecked = value;
            }

            RaiseCommands();
        }
    }

    public SeekStepOption? SelectedSeekStepOption
    {
        get => _selectedSeekStepOption;
        set => SetField(ref _selectedSeekStepOption, value);
    }

    public bool MixToMonoPlayback
    {
        get => _mixToMonoPlayback;
        set
        {
            if (SetField(ref _mixToMonoPlayback, value))
            {
                _playbackService.SetMixToMono(value);
            }
        }
    }

    public bool IsTranscribing
    {
        get => _isTranscribing;
        private set
        {
            if (SetField(ref _isTranscribing, value))
            {
                TranscribeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void BeginSeek() => _isSeekingByUser = true;

    public void EndSeek()
    {
        _isSeekingByUser = false;
        if (!_playbackService.IsLoaded)
        {
            return;
        }

        _playbackService.Seek(TimeSpan.FromSeconds(SeekSeconds));
        UpdatePositionText();
    }

    public async Task ReloadAsync(string? newFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(newFilePath))
        {
            try
            {
                await _catalogService.AddOrUpdateFileAsync(newFilePath);
            }
            catch (Exception ex)
            {
                StatusText = $"追加失敗: {ex.Message}";
            }
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var oldItems = Items.ToList();
            foreach (var item in oldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
            var list = await Task.Run(async () => await _catalogService.GetAllAsync());
            Items.Clear();
            foreach (var item in list)
            {
                Items.Add(item);
                item.PropertyChanged += OnItemPropertyChanged;
            }

            if (SelectedItem is not null)
            {
                SelectedItem = Items.FirstOrDefault(x => x.FilePath == SelectedItem.FilePath);
            }

            UpdateAllItemsCheckedState();
            StatusText = $"{Items.Count} 件";
        }
        catch (Exception ex)
        {
            StatusText = $"読み込み失敗: {ex.Message}";
        }
    }

    private async Task AddFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ライブラリへ追加するFLACを選択",
            Filter = "FLAC (*.flac)|*.flac",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var added = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                await Task.Run(async () => await _catalogService.AddOrUpdateFileAsync(file));
                added++;
            }
            catch (Exception ex)
            {
                StatusText = $"追加失敗: {ex.Message}";
            }
        }

        await RefreshAsync();
        if (added > 0)
        {
            StatusText = $"{added} 件追加しました。";
        }
    }

    private void LoadSelectedForPlayback()
    {
        StopPlaybackState();

        if (SelectedItem is null || !File.Exists(SelectedItem.FilePath))
        {
            DurationSeconds = 0;
            SeekSeconds = 0;
            PositionText = "00:00 / 00:00";
            return;
        }

        try
        {
            _playbackService.Load(SelectedItem.FilePath);
            _playbackService.SetGains(SpeakerGainDb, MicGainDb);
            _playbackService.SetMixToMono(MixToMonoPlayback);
            DurationSeconds = Math.Max(0, _playbackService.Duration.TotalSeconds);
            SeekSeconds = 0;
            UpdatePositionText();
        }
        catch (Exception ex)
        {
            StatusText = $"再生準備失敗: {ex.Message}";
        }

        RaiseCommands();
    }

    private async Task TogglePlaybackAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!await EnsureFileExistsOrPromptRemoveAsync("再生", SelectedItem.FilePath))
        {
            return;
        }

        if (!_playbackService.IsLoaded)
        {
            LoadSelectedForPlayback();
        }

        if (!_playbackService.IsLoaded)
        {
            return;
        }

        if (_playbackService.IsPlaying)
        {
            _playbackService.Pause();
            _positionTimer.Stop();
            IsPlaying = false;
            PlaybackButtonText = "再生";
        }
        else
        {
            _playbackService.Play();
            _positionTimer.Start();
            IsPlaying = true;
            PlaybackButtonText = "一時停止";
        }

        RaiseCommands();
    }

    private Task StopAsync()
    {
        _playbackService.Stop();
        StopPlaybackState();
        SeekSeconds = 0;
        UpdatePositionText();
        return Task.CompletedTask;
    }

    private async Task SaveTitleAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!await EnsureFileExistsOrPromptRemoveAsync("タイトル保存", SelectedItem.FilePath))
        {
            return;
        }

        var title = EditableTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText = "タイトルが空です。";
            return;
        }

        try
        {
            await _catalogService.UpdateTitleAsync(SelectedItem.FilePath, title);
            await RefreshAsync();
            StatusText = "タイトルを更新しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"タイトル更新失敗: {ex.Message}";
        }
    }

    private async Task RenameAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!await EnsureFileExistsOrPromptRemoveAsync("リネーム", SelectedItem.FilePath))
        {
            return;
        }

        try
        {
            var newPath = await _catalogService.RenameAsync(SelectedItem.FilePath, EditableFileName);
            await RefreshAsync();
            SelectedItem = Items.FirstOrDefault(x => x.FilePath == newPath);
            StatusText = "ファイル名を変更しました。";
        }
        catch (IOException ex)
        {
            ModernDialog.Show(
                $"同名のファイルが既に存在するため、リネームできません。\n{ex.Message}",
                "リネーム失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                MessageBoxResult.OK);
            StatusText = $"リネーム失敗: {ex.Message}";
        }
        catch (Exception ex)
        {
            ModernDialog.Show(
                ex.Message,
                "リネーム失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                MessageBoxResult.OK);
            StatusText = $"リネーム失敗: {ex.Message}";
        }
    }

    private async Task DeleteFileAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!await EnsureFileExistsOrPromptRemoveAsync("ファイル削除", SelectedItem.FilePath))
        {
            return;
        }

        var result = ModernDialog.Show(
            $"ファイルを削除します。\n{SelectedItem.FileName}",
            "削除確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _catalogService.DeleteFileAsync(SelectedItem.FilePath);
            await RefreshAsync();
            StatusText = "ファイルを削除しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"削除失敗: {ex.Message}";
        }
    }

    private async Task RemoveMissingFromListAsync()
    {
        var missing = Items.Where(x => !File.Exists(x.FilePath)).ToList();
        if (missing.Count == 0)
        {
            StatusText = "削除対象の欠損ファイル情報はありません。";
            return;
        }

        var result = ModernDialog.Show(
            $"実ファイルが見つからない {missing.Count} 件を一覧から削除します。",
            "一括削除確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            foreach (var item in missing)
            {
                await _catalogService.RemoveFromListAsync(item.FilePath);
            }

            await RefreshAsync();
            StatusText = $"{missing.Count} 件を一覧から削除しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"一括削除失敗: {ex.Message}";
        }
    }

    private async Task OpenInExplorerAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }
        if (!await EnsureFileExistsOrPromptRemoveAsync("Explorer表示", SelectedItem.FilePath))
        {
            return;
        }
        try
        {
            var args = $"/select,\"{SelectedItem.FilePath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Explorer起動失敗: {ex.Message}";
        }
    }
    private Task OpenTranscriptionFileAsync()
    {
        if (SelectedItem is null)
        {
            return Task.CompletedTask;
        }
        var options = _optionsProvider();
        if (!TryGetExistingTranscriptionFilePath(SelectedItem.FilePath, options.TranscriptionModel, out var outputPath))
        {
            StatusText = "指定モデルの文字起こしファイルが見つかりません。";
            return Task.CompletedTask;
        }
        try
        {
            Process.Start(new ProcessStartInfo(outputPath!)
            {
                UseShellExecute = true
            });
            StatusText = $"文字起こしファイルを開きました: {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"文字起こしファイルを開けませんでした: {ex.Message}";
        }
        return Task.CompletedTask;
    }
    private bool CanOpenTranscriptionFile()
    {
        if (SelectedItem is null)
        {
            return false;
        }
        var options = _optionsProvider();
        return TryGetExistingTranscriptionFilePath(SelectedItem.FilePath, options.TranscriptionModel, out _);
    }
    private bool CanTranscribe()
    {
        if (SelectedItem is null)
        {
            return false;
        }
        var options = _optionsProvider();
        return !IsTranscribing
            && options.TranscriptionEnabled
            && !TryGetExistingTranscriptionFilePath(SelectedItem.FilePath, options.TranscriptionModel, out _);
    }
    public void NotifyOptionsChanged()
    {
        TranscribeCommand.RaiseCanExecuteChanged();
        OpenTranscriptionFileCommand.RaiseCanExecuteChanged();
    }
    private async Task TranscribeAsync()
    {
        try
        {
            if (SelectedItem is null)
            {
                return;
            }

            if (!await EnsureFileExistsOrPromptRemoveAsync("文字起こし", SelectedItem.FilePath))
            {
                return;
            }

            var options = _optionsProvider();
            if (!options.TranscriptionEnabled)
            {
                StatusText = "文字起こし機能が無効です。設定画面で有効化してください。";
                return;
            }

            var queued = _transcriptionQueue.TryEnqueue(new TranscriptionJobRequest(
                AudioFilePath: SelectedItem.FilePath,
                Options: options,
                Trigger: TranscriptionTrigger.Manual));
            if (!queued)
            {
                StatusText = "文字起こしキューへの投入に失敗しました。";
                return;
            }

            IsTranscribing = true;
            StatusText = "文字起こしジョブをキューへ追加しました。";
        }
        catch (Exception ex)
        {
            IsTranscribing = false;
            StatusText = $"文字起こし開始時に例外が発生しました: {ex.Message}";
            ModernDialog.Show(
                $"文字起こし開始時に例外が発生しました。\n{ex.Message}",
                "文字起こしエラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnTranscriptionJobCompleted(object? sender, TranscriptionJobCompletedEventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }
        app.Dispatcher.Invoke(() =>
        {
            if (e.Request.Trigger == TranscriptionTrigger.Manual)
            {
                IsTranscribing = false;
            }
            if (e.Result.Succeeded)
            {
                StatusText = $"文字起こし完了: {Path.GetFileName(e.Request.AudioFilePath)}";
                if (e.Request.Options.TranscriptionToastNotificationEnabled)
                {
                    AppNotificationHub.Notify("VoxArchive", $"文字起こし完了: {Path.GetFileName(e.Request.AudioFilePath)}", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                StatusText = $"文字起こし失敗: {e.Result.Message}";
                if (e.Request.Options.TranscriptionToastNotificationEnabled)
                {
                    AppNotificationHub.Notify("VoxArchive", $"文字起こし失敗: {e.Result.Message}", System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            RaiseCommands();
        });
    }
    private Task SeekBackwardAsync() => SeekRelativeAsync(-1);

    private Task SeekForwardAsync() => SeekRelativeAsync(1);

    private async Task SeekRelativeAsync(int direction)
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!await EnsureFileExistsOrPromptRemoveAsync("シーク", SelectedItem.FilePath))
        {
            return;
        }

        if (!_playbackService.IsLoaded)
        {
            LoadSelectedForPlayback();
        }

        if (!_playbackService.IsLoaded)
        {
            return;
        }

        var stepSeconds = SelectedSeekStepOption?.Seconds ?? 10;
        var targetSeconds = Math.Clamp(SeekSeconds + (direction * stepSeconds), 0d, DurationSeconds);
        SeekSeconds = targetSeconds;
        UpdatePositionText();
    }

    private async Task RemoveFromListAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            await _catalogService.RemoveFromListAsync(SelectedItem.FilePath);
            await RefreshAsync();
            StatusText = "一覧から削除しました（ファイルは残ります）。";
        }
        catch (Exception ex)
        {
            StatusText = $"一覧削除失敗: {ex.Message}";
        }
    }

    private async Task<bool> EnsureFileExistsOrPromptRemoveAsync(string actionName, string filePath)
    {
        if (File.Exists(filePath))
        {
            return true;
        }

        await HandleMissingFileAsync(actionName, filePath);
        return false;
    }

    private async Task HandleMissingFileAsync(string actionName, string filePath)
    {
        var result = ModernDialog.Show(
            $"{actionName}の対象ファイルが見つかりません。\n{filePath}\n\n一覧から削除しますか？",
            "ファイル未検出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            StatusText = "ファイルが見つかりません。";
            return;
        }

        try
        {
            await _catalogService.RemoveFromListAsync(filePath);
            await RefreshAsync();
            StatusText = "一覧から削除しました（ファイルは残ります）。";
        }
        catch (Exception ex)
        {
            StatusText = $"一覧削除失敗: {ex.Message}";
        }
    }


    private List<LibraryRecordingItem> GetCheckedItems()
    {
        return Items.Where(x => x.IsChecked).ToList();
    }

    private bool CanRemoveCheckedFromList() => GetCheckedItems().Count > 0;

    private bool CanDeleteCheckedFiles() => GetCheckedItems().Count > 0;

    private async Task RemoveCheckedFromListAsync()
    {
        var checkedItems = GetCheckedItems();
        if (checkedItems.Count == 0)
        {
            return;
        }

        var result = ModernDialog.Show(
            $"チェックした {checkedItems.Count} 件を一覧から削除します（ファイルは残ります）。",
            "一括削除確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            foreach (var item in checkedItems)
            {
                await _catalogService.RemoveFromListAsync(item.FilePath);
            }

            await RefreshAsync();
            StatusText = $"{checkedItems.Count} 件を一覧から削除しました（ファイルは残ります）。";
        }
        catch (Exception ex)
        {
            StatusText = $"一覧一括削除失敗: {ex.Message}";
        }
    }

    private async Task DeleteCheckedFilesAsync()
    {
        var checkedItems = GetCheckedItems();
        if (checkedItems.Count == 0)
        {
            return;
        }

        var result = ModernDialog.Show(
            $"チェックした {checkedItems.Count} 件のファイルを削除します。",
            "一括ファイル削除確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            foreach (var item in checkedItems)
            {
                await _catalogService.DeleteFileAsync(item.FilePath);
            }

            await RefreshAsync();
            StatusText = $"{checkedItems.Count} 件のファイルを削除しました。";
        }
        catch (Exception ex)
        {
            StatusText = $"ファイル一括削除失敗: {ex.Message}";
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryRecordingItem.IsChecked))
        {
            return;
        }

        UpdateAllItemsCheckedState();
        RaiseCommands();
    }

    private void UpdateAllItemsCheckedState()
    {
        var allChecked = Items.Count > 0 && Items.All(x => x.IsChecked);
        if (_allItemsChecked != allChecked)
        {
            _allItemsChecked = allChecked;
            OnPropertyChanged(nameof(AllItemsChecked));
        }
    }

    private void UpdatePositionFromPlayer()
    {
        if (_isSeekingByUser)
        {
            return;
        }

        _isUpdatingFromPlayer = true;
        try
        {
            SeekSeconds = _playbackService.Position.TotalSeconds;
        }
        finally
        {
            _isUpdatingFromPlayer = false;
        }
    }

    private void UpdatePositionText()
    {
        var pos = TimeSpan.FromSeconds(SeekSeconds);
        var dur = TimeSpan.FromSeconds(DurationSeconds);
        var format = dur.TotalHours >= 1d ? @"hh\:mm\:ss" : @"mm\:ss";
        PositionText = $"{pos.ToString(format)} / {dur.ToString(format)}";
    }

    private void StopPlaybackState()
    {
        _positionTimer.Stop();
        IsPlaying = false;
        PlaybackButtonText = "再生";
    }

    private void RaiseCommands()
    {
        TogglePlaybackCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SaveTitleCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        DeleteFileCommand.RaiseCanExecuteChanged();
        RemoveFromListCommand.RaiseCanExecuteChanged();
        RemoveCheckedFromListCommand.RaiseCanExecuteChanged();
        DeleteCheckedFilesCommand.RaiseCanExecuteChanged();
        OpenInExplorerCommand.RaiseCanExecuteChanged();
        OpenTranscriptionFileCommand.RaiseCanExecuteChanged();
        TranscribeCommand.RaiseCanExecuteChanged();
        SeekBackwardCommand.RaiseCanExecuteChanged();
        SeekForwardCommand.RaiseCanExecuteChanged();
    }

    private static bool TryGetExistingTranscriptionFilePath(string audioFilePath, TranscriptionModel model, out string? outputPath)
    {
        outputPath = null;
        var candidates = WhisperTranscriptionService.BuildOutputPaths(audioFilePath, model, AllTranscriptionOutputFormats);
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            outputPath = path;
            return true;
        }

        return false;
    }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed record SeekStepOption(int Seconds, string Label);

    public void Dispose()
    {
        _transcriptionQueue.JobCompleted -= OnTranscriptionJobCompleted;
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _positionTimer.Stop();
        _playbackService.Dispose();
    }
}









