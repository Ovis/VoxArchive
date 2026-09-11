using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorのセッション内状態を管理する。
/// </summary>
/// <remarks>
/// 永続化は行わず、CutRange・Gain・Muteだけを<see cref="AudioEditHistory"/>へ記録する。
/// 波形解析データや選択範囲はUndo/Dirty対象に含めない。
/// </remarks>
public sealed class AudioEditorViewModel : INotifyPropertyChanged
{
    private AudioEditHistory? _history;
    private AudioEditState? _workingState;
    private bool _gainAdjustmentActive;
    private bool _isAnalyzing = true;
    private double _analysisProgress;
    private string _statusText = "波形を解析しています...";
    private TimeSpan? _selectionStart;
    private TimeSpan? _selectionEnd;
    private AudioCutRangeRow? _selectedCutRange;

    public AudioEditorViewModel(LibraryRecordingItem item)
    {
        SourceItem = item ?? throw new ArgumentNullException(nameof(item));
        CutSelectionCommand = new DelegateCommand(CutSelectionAsync, () => IsReady && HasSelection);
        RemoveSelectedCutCommand = new DelegateCommand(RemoveSelectedCutAsync, () => IsReady && SelectedCutRange is not null);
        UndoCommand = new DelegateCommand(UndoAsync, () => _history?.CanUndo == true);
        RedoCommand = new DelegateCommand(RedoAsync, () => _history?.CanRedo == true);
        ClearSelectionCommand = new DelegateCommand(() =>
        {
            ClearSelection();
            return Task.CompletedTask;
        }, () => HasSelection);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? EditStateChanged;

    public LibraryRecordingItem SourceItem { get; }
    public string SourceFilePath => SourceItem.FilePath;
    public string SourceTitle => string.IsNullOrWhiteSpace(SourceItem.Title) ? SourceItem.FileName : SourceItem.Title;
    public AudioWaveformAnalysisResult? Waveform { get; private set; }
    public ObservableCollection<AudioCutRangeRow> CutRanges { get; } = new();

    public DelegateCommand CutSelectionCommand { get; }
    public DelegateCommand RemoveSelectedCutCommand { get; }
    public DelegateCommand UndoCommand { get; }
    public DelegateCommand RedoCommand { get; }
    public DelegateCommand ClearSelectionCommand { get; }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set => SetField(ref _isAnalyzing, value);
    }

    public bool IsReady => !IsAnalyzing && _history is not null && Waveform is not null;
    public AudioEditState? CurrentState => EffectiveState;

    public double AnalysisProgress
    {
        get => _analysisProgress;
        private set => SetField(ref _analysisProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool IsDirty => _history?.IsDirty == true;
    public bool IsStereo => EffectiveState?.ChannelCount == 2;
    public TimeSpan SourceDuration => EffectiveState?.SourceDuration ?? TimeSpan.Zero;
    public TimeSpan EditedDuration => EffectiveState?.EstimatedOutputDuration ?? TimeSpan.Zero;
    public string SourceDurationText => FormatTime(SourceDuration);
    public string EditedDurationText => FormatTime(EditedDuration);
    public string DirtyText => IsDirty ? "未書き出しの変更あり" : "変更なし";

    public bool HasSelection => _selectionStart.HasValue && _selectionEnd.HasValue && _selectionEnd > _selectionStart;
    public TimeSpan? SelectionStart => _selectionStart;
    public TimeSpan? SelectionEnd => _selectionEnd;
    public string SelectionText => HasSelection
        ? $"{FormatTime(_selectionStart!.Value)} - {FormatTime(_selectionEnd!.Value)}  ({FormatTime(_selectionEnd.Value - _selectionStart.Value)})"
        : "選択なし";

    public AudioCutRangeRow? SelectedCutRange
    {
        get => _selectedCutRange;
        set
        {
            if (SetField(ref _selectedCutRange, value))
            {
                RemoveSelectedCutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double Channel1GainDb
    {
        get => EffectiveState?.Channels[0].GainDb ?? 0d;
        set => SetGain(0, value);
    }

    public double Channel2GainDb
    {
        get => IsStereo ? EffectiveState!.Channels[1].GainDb : 0d;
        set
        {
            if (IsStereo) SetGain(1, value);
        }
    }

    public bool Channel1Muted
    {
        get => EffectiveState?.Channels[0].IsMuted == true;
        set => SetMute(0, value);
    }

    public bool Channel2Muted
    {
        get => IsStereo && EffectiveState!.Channels[1].IsMuted;
        set
        {
            if (IsStereo) SetMute(1, value);
        }
    }

    private AudioEditState? EffectiveState => _workingState ?? _history?.Current;

    public void CompleteAnalysis(AudioWaveformAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Waveform = result;
        _history = new AudioEditHistory(new AudioEditState(result.Duration, result.Channels));
        _workingState = null;
        AnalysisProgress = 1d;
        IsAnalyzing = false;
        StatusText = result.PeakAbsoluteSample > 1d
            ? $"波形解析完了。元音声ピーク {result.PeakAbsoluteSample:F3} (0dBFS超過あり)"
            : "波形解析が完了しました。";
        RefreshStateProperties();
        OnPropertyChanged(nameof(Waveform));
        OnPropertyChanged(nameof(IsReady));
    }

    public void FailAnalysis(Exception exception)
    {
        IsAnalyzing = false;
        StatusText = $"波形解析に失敗しました: {exception.Message}";
        OnPropertyChanged(nameof(IsReady));
    }

    public void ReportAnalysisProgress(double progress)
    {
        AnalysisProgress = Math.Clamp(progress, 0d, 1d);
        StatusText = $"波形を解析しています... {AnalysisProgress:P0}";
    }

    public void SetSelection(TimeSpan first, TimeSpan second)
    {
        var start = first <= second ? first : second;
        var end = first <= second ? second : first;
        var duration = SourceDuration;
        start = start < TimeSpan.Zero ? TimeSpan.Zero : start > duration ? duration : start;
        end = end < TimeSpan.Zero ? TimeSpan.Zero : end > duration ? duration : end;
        _selectionStart = start;
        _selectionEnd = end;
        RaiseSelectionProperties();
    }

    public void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        RaiseSelectionProperties();
    }

    public void BeginGainAdjustment()
    {
        if (_history is null || _gainAdjustmentActive) return;
        _gainAdjustmentActive = true;
        _workingState = _history.Current;
    }

    public void CommitGainAdjustment()
    {
        if (!_gainAdjustmentActive || _history is null) return;
        _gainAdjustmentActive = false;
        if (_workingState is not null)
        {
            _history.Apply(_workingState);
        }
        _workingState = null;
        RefreshStateProperties();
    }

    /// <summary>
    /// 書き出し成功時点を新しいClean baselineにする。Undo/Redo履歴自体は維持する。
    /// </summary>
    public void MarkExported()
    {
        CommitGainAdjustment();
        _history?.MarkClean();
        RefreshStateProperties();
    }

    private Task CutSelectionAsync()
    {
        if (_history is null || !HasSelection) return Task.CompletedTask;
        var range = new AudioCutRange(_selectionStart!.Value, _selectionEnd!.Value);
        _history.Apply(_history.Current.AddCutRange(range));
        ClearSelection();
        RefreshStateProperties();
        StatusText = "選択範囲をカットしました。";
        return Task.CompletedTask;
    }

    private Task RemoveSelectedCutAsync()
    {
        if (_history is null || SelectedCutRange is null) return Task.CompletedTask;
        _history.Apply(_history.Current.RemoveCutRange(SelectedCutRange.Range));
        SelectedCutRange = null;
        RefreshStateProperties();
        StatusText = "カット区間を削除しました。";
        return Task.CompletedTask;
    }

    private Task UndoAsync()
    {
        if (_history?.Undo() == true)
        {
            _workingState = null;
            RefreshStateProperties();
            StatusText = "元に戻しました。";
        }
        return Task.CompletedTask;
    }

    private Task RedoAsync()
    {
        if (_history?.Redo() == true)
        {
            _workingState = null;
            RefreshStateProperties();
            StatusText = "やり直しました。";
        }
        return Task.CompletedTask;
    }

    private void SetGain(int channelIndex, double gainDb)
    {
        if (_history is null) return;
        gainDb = Math.Clamp(gainDb, -60d, 20d);
        var basis = EffectiveState ?? _history.Current;
        var old = basis.Channels[channelIndex];
        var next = basis.WithChannelState(channelIndex, new AudioChannelEditState(gainDb, old.IsMuted));

        if (_gainAdjustmentActive)
        {
            _workingState = next;
            RefreshChannelProperties();
            return;
        }

        _history.Apply(next);
        RefreshStateProperties();
    }

    private void SetMute(int channelIndex, bool muted)
    {
        if (_history is null) return;
        CommitGainAdjustment();
        var old = _history.Current.Channels[channelIndex];
        _history.Apply(_history.Current.WithChannelState(channelIndex, new AudioChannelEditState(old.GainDb, muted)));
        RefreshStateProperties();
    }

    private void RefreshStateProperties()
    {
        CutRanges.Clear();
        if (_history is not null)
        {
            foreach (var range in _history.Current.CutRanges)
            {
                CutRanges.Add(new AudioCutRangeRow(range));
            }
        }

        RefreshChannelProperties();
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(SourceDuration));
        OnPropertyChanged(nameof(EditedDuration));
        OnPropertyChanged(nameof(SourceDurationText));
        OnPropertyChanged(nameof(EditedDurationText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtyText));
        OnPropertyChanged(nameof(IsStereo));
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        CutSelectionCommand.RaiseCanExecuteChanged();
        EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshChannelProperties()
    {
        OnPropertyChanged(nameof(Channel1GainDb));
        OnPropertyChanged(nameof(Channel2GainDb));
        OnPropertyChanged(nameof(Channel1Muted));
        OnPropertyChanged(nameof(Channel2Muted));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectionStart));
        OnPropertyChanged(nameof(SelectionEnd));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        CutSelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    public static string FormatTime(TimeSpan value)
        => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record AudioCutRangeRow(AudioCutRange Range)
{
    public string StartText => AudioEditorViewModel.FormatTime(Range.Start);
    public string EndText => AudioEditorViewModel.FormatTime(Range.End);
    public string DurationText => AudioEditorViewModel.FormatTime(Range.Duration);
    public string DisplayText => $"{StartText}  -  {EndText}   ({DurationText})";
}
