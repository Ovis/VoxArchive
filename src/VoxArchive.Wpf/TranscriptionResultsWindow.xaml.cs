namespace VoxArchive.Wpf;

/// <summary>
/// 選択中の録音に対する文字起こし結果を、Libraryのサイズに依存しない独立ウィンドウで表示する
/// </summary>
public partial class TranscriptionResultsWindow : System.Windows.Window
{
    /// <summary>
    /// Libraryと同じ文字起こし結果状態を表示するウィンドウを生成する
    /// </summary>
    /// <param name="state">Libraryで保持している文字起こし結果一覧・選択状態</param>
    public TranscriptionResultsWindow(LibraryTranscriptionResultsState state)
    {
        InitializeComponent();
        ResultsPanel.State = state;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnCloseButtonClick(object sender, System.Windows.RoutedEventArgs e)
        => Close();
}
