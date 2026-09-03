using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで文字起こし結果の一覧・メタデータ・本文を表示するパネル
/// </summary>
/// <remarks>
/// 結果の発見と本文ロードは <see cref="LibraryTranscriptionResultsState"/> に委譲し、
/// 本クラスは選択操作を状態へ伝えるUI責務だけを持つ。同じパネルをLibrary内と独立Windowで再利用する。
/// </remarks>
public partial class LibraryTranscriptionResultsPanel : UserControl
{
    private LibraryTranscriptionResultsState? _state;
    private bool _selectionChangeInProgress;

    /// <summary>
    /// 結果件数からComboBoxの有効状態へ変換するコンバーターを取得する
    /// </summary>
    public static IValueConverter CountToEnabledConverter { get; } = new PositiveCountConverter();

    /// <summary>
    /// パネル上部の「文字起こし」見出しを表示するかどうかを取得または設定する
    /// </summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>
    /// 本文表示領域の高さを取得または設定する。独立WindowではAutoを指定して残り領域を使用する
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
    /// 指定されたライブラリ文字起こし状態を表示するパネルを生成する
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

    private sealed class PositiveCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
