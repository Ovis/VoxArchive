namespace VoxArchive.Wpf;

/// <summary>
/// 録音ライブラリの表示と、ウィンドウ固有のUIイベントを仲介する
/// </summary>
public partial class LibraryWindow : System.Windows.Window
{
    private readonly LibraryViewModel _viewModel;
    private readonly LibraryTranscriptionResultsCoordinator _transcriptionResultsCoordinator;

    public LibraryWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _transcriptionResultsCoordinator = new LibraryTranscriptionResultsCoordinator(viewModel);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// XAMLから参照する文字起こし結果の状態を取得する
    /// </summary>
    /// <remarks>
    /// 既存のLibrary全体のDataContextは <see cref="LibraryViewModel"/> のまま維持し、
    /// 文字起こし結果UIだけがWindow経由でこの状態を参照する。既存Bindingへの影響を避けるためである。
    /// </remarks>
    public LibraryTranscriptionResultsState TranscriptionResults => _transcriptionResultsCoordinator.State;

    /// <summary>
    /// 結果セレクタから選択された文字起こし結果を読み込む
    /// </summary>
    public async Task SelectTranscriptionResultAsync(LibraryTranscriptionResultItem? result)
        => await _transcriptionResultsCoordinator.SelectAsync(result);

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            await _transcriptionResultsCoordinator.InitializeAsync();
        }
        catch
        {
            // Library本体は文字起こし結果が壊れていても利用できる必要があるため、起動失敗にはしない。
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _transcriptionResultsCoordinator.Dispose();
        _viewModel.Dispose();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnTitleBarCloseButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Close();
    }

    private void OnSeekDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.BeginSeek();
    }

    private void OnSeekDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.EndSeek();
    }

    private void OnRecordingGridMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as System.Windows.DependencyObject;
        var row = FindParent<System.Windows.Controls.DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        if (_viewModel.TogglePlaybackCommand.CanExecute(null))
        {
            _viewModel.TogglePlaybackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRecordingGridPreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as System.Windows.DependencyObject;
        var row = FindParent<System.Windows.Controls.DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        row.IsSelected = true;
        row.Focus();
    }

    private static T? FindParent<T>(System.Windows.DependencyObject? child)
        where T : System.Windows.DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
