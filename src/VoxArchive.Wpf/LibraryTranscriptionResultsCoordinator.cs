using System.ComponentModel;

namespace VoxArchive.Wpf;

/// <summary>
/// LibraryViewModelの録音選択・文字起こし状態と、文字起こし結果状態を同期する
/// </summary>
/// <remarks>
/// <see cref="LibraryTranscriptionResultsState"/> 自体はファイル発見と選択結果の読み込みだけを担当するため、
/// Library固有のライフサイクル監視をこのクラスへ分離する。これにより結果UIを追加しても既存の再生・編集ロジックへ
/// 文字起こしファイル走査の責務を混在させずに済む。
/// </remarks>
public sealed class LibraryTranscriptionResultsCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryViewModel _libraryViewModel;
    private bool _wasTranscribing;
    private bool _disposed;

    /// <summary>
    /// Libraryの選択状態と同期するCoordinatorを生成する
    /// </summary>
    public LibraryTranscriptionResultsCoordinator(LibraryViewModel libraryViewModel)
    {
        _libraryViewModel = libraryViewModel;
        State = new LibraryTranscriptionResultsState(
            new TranscriptionResultDiscoveryService(),
            new TranscriptionDocumentStore());
        _wasTranscribing = libraryViewModel.IsTranscribing;
        _libraryViewModel.PropertyChanged += OnLibraryPropertyChanged;
        State.PropertyChanged += OnStatePropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// UIから参照する文字起こし結果状態を取得する
    /// </summary>
    public LibraryTranscriptionResultsState State { get; }

    /// <summary>
    /// 現在選択中の録音に対する結果一覧を初期化する
    /// </summary>
    public Task InitializeAsync()
        => State.LoadForRecordingAsync(_libraryViewModel.SelectedItem?.FilePath);

    /// <summary>
    /// UIで指定された結果を選択し、本文を遅延読み込みする
    /// </summary>
    public Task SelectAsync(LibraryTranscriptionResultItem? result)
        => result is null ? Task.CompletedTask : State.SelectAsync(result);

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.SelectedItem))
        {
            _ = ReloadForSelectionAsync();
            return;
        }

        if (e.PropertyName != nameof(LibraryViewModel.IsTranscribing)) return;

        var isTranscribing = _libraryViewModel.IsTranscribing;
        if (_wasTranscribing && !isTranscribing)
        {
            // ジョブ完了通知では成功/失敗にかかわらず状態が解除される。
            // 成功時に追加されたJSONを拾い、失敗時は同じ一覧へ戻るだけなので、ここでは無条件に再走査する。
            _ = RefreshAfterTranscriptionAsync();
        }
        _wasTranscribing = isTranscribing;
    }

    private async Task ReloadForSelectionAsync()
    {
        try
        {
            await State.LoadForRecordingAsync(_libraryViewModel.SelectedItem?.FilePath);
        }
        catch
        {
            // 録音選択そのものは再生・編集にも使うため、文字起こしJSONの読み込み失敗でLibrary操作全体を止めない。
            // 詳細な破損状態の表示は結果UI側のエラー表現を導入する段階で扱う。
        }
    }

    private async Task RefreshAfterTranscriptionAsync()
    {
        try
        {
            await State.RefreshAsync();
        }
        catch
        {
            // 認識ジョブの完了処理を結果一覧更新の失敗で巻き戻さない。
        }
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _libraryViewModel.PropertyChanged -= OnLibraryPropertyChanged;
        State.PropertyChanged -= OnStatePropertyChanged;
    }
}
