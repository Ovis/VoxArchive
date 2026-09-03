using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで文字起こし結果の一覧・メタデータ・本文を表示するパネル
/// </summary>
/// <remarks>
/// 結果の発見と本文ロードは <see cref="LibraryTranscriptionResultsState"/> に委譲し、
/// 本クラスは選択操作と表示先に依存しないUIイベントだけを担当する。
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
}
