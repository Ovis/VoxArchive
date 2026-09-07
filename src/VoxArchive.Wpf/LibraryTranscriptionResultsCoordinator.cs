using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// LibraryViewModelの録音選択・文字起こし状態と、文字起こし結果状態を同期する
/// </summary>
/// <remarks>
/// <see cref="LibraryTranscriptionResultsState"/> はUI選択状態だけを保持し、
/// canonical result操作と再文字起こしはApplication Use Caseへ委譲する。
/// </remarks>
public sealed class LibraryTranscriptionResultsCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryViewModel _libraryViewModel;
    private readonly LibraryRetranscriptionService _retranscriptionService;
    private bool _wasTranscribing;
    private bool _disposed;

    /// <summary>Libraryの選択状態と同期するCoordinatorを生成する</summary>
    public LibraryTranscriptionResultsCoordinator(LibraryViewModel libraryViewModel)
    {
        _libraryViewModel = libraryViewModel;
        var services = ((App)System.Windows.Application.Current).Services;
        State = new LibraryTranscriptionResultsState(services.GetRequiredService<ITranscriptionApplicationService>());
        _retranscriptionService = ActivatorUtilities.CreateInstance<LibraryRetranscriptionService>(services);
        _wasTranscribing = libraryViewModel.IsTranscribing;
        _libraryViewModel.PropertyChanged += OnLibraryPropertyChanged;
        State.PropertyChanged += OnStatePropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public LibraryTranscriptionResultsState State { get; }
    public Task InitializeAsync() => State.LoadForRecordingAsync(_libraryViewModel.SelectedItem?.FilePath);
    public Task SelectAsync(LibraryTranscriptionResultItem? result) => result is null ? Task.CompletedTask : State.SelectAsync(result);

    /// <summary>選択中のcanonical resultを基準に再文字起こしする</summary>
    public async Task RetranscribeSelectedAsync()
    {
        var audioFilePath = _libraryViewModel.SelectedItem?.FilePath ?? throw new InvalidOperationException("録音ファイルが選択されていません。");
        var result = State.SelectedResult ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        if (State.SelectedDocument is null) throw new InvalidOperationException("文字起こし結果を読み込めませんでした。");

        var replaceConfirm = ModernDialog.Show($"{result.DisplayName} を再文字起こしします。\n成功した場合は現在の文字起こし結果を新しい結果で置き換えます。\n失敗またはキャンセルした場合は現在の結果を残します。", "再文字起こし", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel);
        if (replaceConfirm != System.Windows.MessageBoxResult.OK) return;

        var prepared = await _retranscriptionService.PrepareAsync(result.DocumentPath);
        if (prepared.UsedCurrentSettingsFallback)
        {
            var fallbackConfirm = ModernDialog.Show("この文字起こし結果には再実行に必要なEngine固有設定の全ては保存されていません。\n保存済みEngine/Modelを優先し、それ以外は現在の設定で補完して再文字起こしします。", "再文字起こし", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel);
            if (fallbackConfirm != System.Windows.MessageBoxResult.OK) return;
        }

        // 既存canonical JSONは開始時に削除しない。失敗・キャンセル時も以前の正常結果を参照可能にする。
        var enqueueResult = await _retranscriptionService.EnqueueAsync(audioFilePath, prepared);
        if (!enqueueResult.Enqueued)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(enqueueResult.Message)
                ? "この録音は既に文字起こしキューに投入されています。"
                : enqueueResult.Message);
        }
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.SelectedItem))
        {
            _ = ReloadForSelectionAsync();
            return;
        }
        if (e.PropertyName != nameof(LibraryViewModel.IsTranscribing)) return;
        var isTranscribing = _libraryViewModel.IsTranscribing;
        if (_wasTranscribing && !isTranscribing) _ = RefreshAfterTranscriptionAsync();
        _wasTranscribing = isTranscribing;
    }

    private async Task ReloadForSelectionAsync()
    {
        try { await State.LoadForRecordingAsync(_libraryViewModel.SelectedItem?.FilePath); }
        catch
        {
            // 文字起こし結果の読み込み失敗で録音の再生・編集操作全体を止めない。
        }
    }

    private async Task RefreshAfterTranscriptionAsync()
    {
        try { await State.RefreshAsync(); }
        catch
        {
            // 結果一覧更新失敗を認識ジョブの完了処理へ波及させない。
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryTranscriptionResultsState.SummaryText))
        {
            _libraryViewModel.NotifyOptionsChanged();
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _libraryViewModel.PropertyChanged -= OnLibraryPropertyChanged;
        State.PropertyChanged -= OnStatePropertyChanged;
    }
}
