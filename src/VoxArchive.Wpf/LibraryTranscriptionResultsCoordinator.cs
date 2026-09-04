using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace VoxArchive.Wpf;

/// <summary>
/// LibraryViewModelの録音選択・文字起こし状態と、文字起こし結果状態を同期する
/// </summary>
/// <remarks>
/// <see cref="LibraryTranscriptionResultsState"/> 自体はファイル発見・結果操作を担当し、
/// Library固有の選択変更やジョブ完了との同期だけを本クラスへ分離する。
/// </remarks>
public sealed class LibraryTranscriptionResultsCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryViewModel _libraryViewModel;
    private readonly LibraryRetranscriptionService _retranscriptionService;
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
            new TranscriptionDocumentStore(),
            new TranscriptionExportService());

        // LibraryWindowは既存構造上MainViewModelから手動生成されるため、アプリ全体で共有しているQueueを
        // AppのDIコンテナから取得する。Queueを新規生成すると実行状態・重複防止が分断されるため必ずSingletonを再利用する。
        _retranscriptionService = ActivatorUtilities.CreateInstance<LibraryRetranscriptionService>(
            ((App)System.Windows.Application.Current).Services);

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

    /// <summary>
    /// 選択中の文字起こし結果を、保存済みのEngine/Model/requested optionsを基準に再文字起こしする
    /// </summary>
    public async Task RetranscribeSelectedAsync()
    {
        var audioFilePath = _libraryViewModel.SelectedItem?.FilePath
            ?? throw new InvalidOperationException("録音ファイルが選択されていません。");
        var result = State.SelectedResult
            ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        var document = State.SelectedDocument
            ?? throw new InvalidOperationException("文字起こし結果を読み込めませんでした。");

        var replaceConfirm = ModernDialog.Show(
            $"{result.DisplayName} を再文字起こしします。\n成功した場合は現在の文字起こし結果を新しい結果で置き換えます。\n失敗またはキャンセルした場合は現在の結果を残します。",
            "再文字起こし",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel);
        if (replaceConfirm != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        var prepared = await _retranscriptionService.PrepareAsync(audioFilePath, document, result.IsLegacy);
        if (prepared.UsedCurrentSettingsFallback)
        {
            var fallbackConfirm = ModernDialog.Show(
                "この文字起こし結果には再実行に必要な設定の一部が保存されていません。\n不足分は現在のWhisper設定で補完して再文字起こしします。",
                "再文字起こし",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Cancel);
            if (fallbackConfirm != System.Windows.MessageBoxResult.OK)
            {
                return;
            }
        }

        // 既存のcanonical JSONはジョブ開始時には削除しない。
        // 認識に失敗・キャンセルした場合も以前の正常結果をLibraryで参照し続けられるようにする。
        if (!_retranscriptionService.TryEnqueue(prepared.Request))
        {
            throw new InvalidOperationException("この録音は既に文字起こしキューに投入されています。");
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
