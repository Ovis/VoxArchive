using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VoxArchive.Wpf;

public sealed class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecordingCatalogService _catalogService;
    private readonly RecordingPlaybackService _playbackService;
    private readonly DispatcherTimer _positionTimer;

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
    private bool _isSeekingByUser;
    private bool _isUpdatingFromPlayer;

    public LibraryViewModel(RecordingCatalogService catalogService, double defaultSpeakerGainDb = 0d, double defaultMicGainDb = 0d)
    {
        _catalogService = catalogService;
        _playbackService = new RecordingPlaybackService();
        _playbackService.PlaybackStopped += (_, _) =>
        {
            IsPlaying = false;
            PlaybackButtonText = "再生";
        };

        _speakerGainDb = defaultSpeakerGainDb;
        _micGainDb = defaultMicGainDb;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _positionTimer.Tick += (_, _) => UpdatePositionFromPlayer();

        Items = new ObservableCollection<LibraryRecordingItem>();

        RefreshCommand = new DelegateCommand(RefreshAsync);
        AddFileCommand = new DelegateCommand(AddFileAsync);
        RemoveMissingFromListCommand = new DelegateCommand(RemoveMissingFromListAsync);
        TogglePlaybackCommand = new DelegateCommand(TogglePlaybackAsync, () => SelectedItem is not null);
        StopCommand = new DelegateCommand(StopAsync, () => _playbackService.IsLoaded);
        SaveTitleCommand = new DelegateCommand(SaveTitleAsync, () => SelectedItem is not null);
        RenameCommand = new DelegateCommand(RenameAsync, () => SelectedItem is not null);
        DeleteFileCommand = new DelegateCommand(DeleteFileAsync, () => SelectedItem is not null);
        RemoveFromListCommand = new DelegateCommand(RemoveFromListAsync, () => SelectedItem is not null);

        _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LibraryRecordingItem> Items { get; }

    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand AddFileCommand { get; }
    public DelegateCommand RemoveMissingFromListCommand { get; }
    public DelegateCommand TogglePlaybackCommand { get; }
    public DelegateCommand StopCommand { get; }
    public DelegateCommand SaveTitleCommand { get; }
    public DelegateCommand RenameCommand { get; }
    public DelegateCommand DeleteFileCommand { get; }
    public DelegateCommand RemoveFromListCommand { get; }

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
            var list = await Task.Run(async () => await _catalogService.GetAllAsync());
            Items.Clear();
            foreach (var item in list)
            {
                Items.Add(item);
            }

            if (SelectedItem is not null)
            {
                SelectedItem = Items.FirstOrDefault(x => x.FilePath == SelectedItem.FilePath);
            }

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
        catch (Exception ex)
        {
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
        PositionText = $"{pos:mm\\:ss} / {dur:mm\\:ss}";
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

    public void Dispose()
    {
        _positionTimer.Stop();
        _playbackService.Dispose();
    }
}



