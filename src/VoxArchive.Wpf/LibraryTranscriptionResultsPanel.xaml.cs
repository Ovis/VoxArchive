using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで文字起こし結果の一覧・メタデータ・本文を表示するパネル
/// </summary>
/// <remarks>
/// 結果の発見と本文ロードは <see cref="LibraryTranscriptionResultsState"/> に委譲し、
/// 本クラスは選択操作を状態へ伝えるUI責務だけを持つ。
/// </remarks>
public partial class LibraryTranscriptionResultsPanel : UserControl
{
    private readonly LibraryTranscriptionResultsState _state;
    private bool _selectionChangeInProgress;

    /// <summary>
    /// 指定されたライブラリ文字起こし状態を表示するパネルを生成する
    /// </summary>
    public LibraryTranscriptionResultsPanel(LibraryTranscriptionResultsState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
    }

    private async void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionChangeInProgress || sender is not ComboBox { SelectedItem: LibraryTranscriptionResultItem selected })
        {
            return;
        }

        // SelectedItemはStateからOneWayで反映しているため、ユーザー操作時だけ明示的に遅延ロードする。
        // 読み込み完了時にState側のSelectedResultが更新されても再入しないようガードする。
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
}
