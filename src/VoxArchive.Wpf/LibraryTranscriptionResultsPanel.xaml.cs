using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで文字起こし結果の一覧・メタデータ・本文と結果操作を表示するパネル
/// </summary>
/// <remarks>
/// 結果の発見・本文ロード・派生出力・正本削除は <see cref="LibraryTranscriptionResultsState"/> に委譲し、
/// 本クラスは選択操作と確認ダイアログなどUI責務だけを持つ。
/// </remarks>
public partial class LibraryTranscriptionResultsPanel : UserControl
{
    private LibraryTranscriptionResultsState? _state;
    private bool _selectionChangeInProgress;

    /// <summary>
    /// エディタで開く操作が要求されたときに発生する
    /// </summary>
    public event EventHandler? OpenInEditorRequested;

    /// <summary>
    /// 独立ウィンドウで開く操作が要求されたときに発生する
    /// </summary>
    public event EventHandler? OpenDetachedRequested;

    /// <summary>
    /// パネル上部の見出しを表示するかどうかを取得または設定する
    /// </summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>
    /// パネル下部の操作ボタンを表示するかどうかを取得または設定する
    /// </summary>
    public bool ShowActions { get; set; } = true;

    /// <summary>
    /// 本文表示領域の高さを取得または設定する
    /// </summary>
    public GridLength TranscriptHeight { get; set; } = new(180);

    /// <summary>
    /// 表示対象の文字起こし状態を取得または設定する
    /// </summary>
    public LibraryTranscriptionResultsState? State
    {
        get => _state;
        set
        {
            _state = value;
            DataContext = value;
        }
    }

    /// <summary>
    /// XAMLから生成するためのパネルを初期化する
    /// </summary>
    public LibraryTranscriptionResultsPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 指定された状態を表示するパネルを生成する
    /// </summary>
    public LibraryTranscriptionResultsPanel(LibraryTranscriptionResultsState state) : this()
    {
        State = state;
    }

    private async void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionChangeInProgress ||
            _state is null ||
            sender is not ComboBox { SelectedItem: LibraryTranscriptionResultItem selected })
        {
            return;
        }

        try
        {
            _selectionChangeInProgress = true;
            await _state.SelectAsync(selected);
        }
        finally
        {
            _selectionChangeInProgress = false;
        }
    }

    private void OnOpenInEditorClick(object sender, RoutedEventArgs e)
        => OpenInEditorRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenDetachedClick(object sender, RoutedEventArgs e)
        => OpenDetachedRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async void OnExportTxtClick(object sender, RoutedEventArgs e)
        => await ExportAsync(TranscriptionOutputFormats.Txt);

    private async void OnExportSrtClick(object sender, RoutedEventArgs e)
        => await ExportAsync(TranscriptionOutputFormats.Srt);

    private async void OnExportVttClick(object sender, RoutedEventArgs e)
        => await ExportAsync(TranscriptionOutputFormats.Vtt);

    private async void OnExportAllClick(object sender, RoutedEventArgs e)
        => await ExportAsync(TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt | TranscriptionOutputFormats.Vtt);

    private async Task ExportAsync(TranscriptionOutputFormats formats)
    {
        if (_state?.SelectedResult is null)
        {
            return;
        }

        try
        {
            var generated = await _state.ExportSelectedAsync(formats);
            ModernDialog.Show(
                $"{generated.Count} 件の派生ファイルを出力しました。\n{string.Join(Environment.NewLine, generated.Select(Path.GetFileName))}",
                "文字起こし出力",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ModernDialog.Show(
                $"文字起こし結果の出力に失敗しました。\n{ex.Message}",
                "文字起こし出力エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_state?.SelectedResult is not { } selected)
        {
            return;
        }

        var result = ModernDialog.Show(
            $"選択中の文字起こし結果を削除します。\n{selected.DisplayName}\n\nTXT/SRT/VTTなどの派生ファイルは削除せず残します。",
            "文字起こし結果の削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _state.DeleteSelectedAsync();
        }
        catch (Exception ex)
        {
            ModernDialog.Show(
                $"文字起こし結果を削除できませんでした。\n{ex.Message}",
                "文字起こし削除エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
