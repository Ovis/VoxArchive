using System.ComponentModel;

namespace VoxArchive.Wpf;

/// <summary>
/// 録音ライブラリの表示と、ウィンドウ固有のUIイベントを仲介する
/// </summary>
public partial class LibraryWindow : System.Windows.Window
{
    private readonly LibraryViewModel _viewModel;
    private readonly LibraryTranscriptionResultsCoordinator _transcriptionResultsCoordinator;
    private TranscriptionResultsWindow? _transcriptionResultsWindow;
    private System.Windows.Controls.Button? _transcribeButton;

    public LibraryWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _transcriptionResultsCoordinator = new LibraryTranscriptionResultsCoordinator(viewModel);
        _transcriptionResultsCoordinator.State.PropertyChanged += OnTranscriptionResultsPropertyChanged;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// XAMLから参照する文字起こし結果の状態を取得する
    /// </summary>
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
            AttachTranscriptionResultsPanel();
        }
        catch
        {
            // Library本体は文字起こし結果が壊れていても利用できる必要があるため、起動失敗にはしない。
        }
    }

    /// <summary>
    /// 既存の編集領域へ文字起こし結果パネルを追加する
    /// </summary>
    /// <remarks>
    /// 文字起こし結果が存在する場合は結果閲覧を主操作とし、既存の「文字起こし」ボタンは隠す。
    /// エディタ/独立Window操作は結果カード直下へ移動し、狭い右ペインで横3ボタンにならないようにする。
    /// </remarks>
    private void AttachTranscriptionResultsPanel()
    {
        var detailGrid = FindDetailGrid(this);
        if (detailGrid is null) return;

        var editContent = detailGrid.Children
            .OfType<System.Windows.UIElement>()
            .FirstOrDefault(x => System.Windows.Controls.Grid.GetRow(x) == 9 && System.Windows.Controls.Grid.GetRowSpan(x) == 1);
        if (editContent is null || editContent is System.Windows.Controls.ScrollViewer) return;

        var buttonRow = FindDescendants<System.Windows.Controls.StackPanel>(editContent)
            .FirstOrDefault(x => x.Orientation == System.Windows.Controls.Orientation.Horizontal &&
                                 x.Children.OfType<System.Windows.Controls.Button>().Any(b => Equals(b.Content, "文字起こし結果を開く")));
        if (buttonRow is not null)
        {
            _transcribeButton = buttonRow.Children
                .OfType<System.Windows.Controls.Button>()
                .FirstOrDefault(b => Equals(b.Content, "文字起こし"));

            var oldEditorButton = buttonRow.Children
                .OfType<System.Windows.Controls.Button>()
                .FirstOrDefault(b => Equals(b.Content, "文字起こし結果を開く"));
            if (oldEditorButton is not null)
            {
                oldEditorButton.Visibility = System.Windows.Visibility.Collapsed;
            }
            UpdateTranscribeButtonVisibility();
        }

        detailGrid.Children.Remove(editContent);

        // 編集欄と文字起こしパネルで右端の基準を揃える。
        // 外側ScrollViewerのスクロールバー直前まで編集欄だけが伸びると視覚的に段差が出るため、
        // 文字起こしパネルと同じ10pxの右余白を既存編集領域にも与える。
        if (editContent is System.Windows.FrameworkElement editElement)
        {
            editElement.Margin = new System.Windows.Thickness(
                editElement.Margin.Left,
                editElement.Margin.Top,
                10,
                editElement.Margin.Bottom);
        }

        var panel = new LibraryTranscriptionResultsPanel(TranscriptionResults);
        panel.OpenInEditorRequested += OnOpenTranscriptionInEditorRequested;
        panel.OpenDetachedRequested += OnOpenTranscriptionResultsWindowRequested;

        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(editContent);
        stack.Children.Add(panel);

        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            Margin = new System.Windows.Thickness(0, 0, 10, 0),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };

        // 動的生成したScrollViewerはLibraryWindowの暗色ScrollBarスタイルを自動継承しないため明示的に設定する。
        if (FindResource("LibraryScrollBarStyle") is System.Windows.Style scrollBarStyle)
        {
            scrollViewer.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = scrollBarStyle;
        }

        System.Windows.Controls.Grid.SetRow(scrollViewer, 9);
        detailGrid.Children.Add(scrollViewer);
    }

    private void OnTranscriptionResultsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryTranscriptionResultsState.SummaryText))
        {
            UpdateTranscribeButtonVisibility();
        }
    }

    private void UpdateTranscribeButtonVisibility()
    {
        if (_transcribeButton is null) return;
        _transcribeButton.Visibility = TranscriptionResults.Results.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void OnOpenTranscriptionInEditorRequested(object? sender, EventArgs e)
    {
        if (_viewModel.OpenTranscriptionFileCommand.CanExecute(null))
        {
            _viewModel.OpenTranscriptionFileCommand.Execute(null);
        }
    }

    private void OnOpenTranscriptionResultsWindowRequested(object? sender, EventArgs e)
        => OpenTranscriptionResultsWindow();

    private static System.Windows.Controls.Grid? FindDetailGrid(System.Windows.DependencyObject root)
    {
        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Grid grid &&
                grid.RowDefinitions.Count == 10 &&
                grid.Children.OfType<System.Windows.Controls.TextBlock>().Any(x => x.Text == "再生"))
            {
                return grid;
            }

            var nested = FindDetailGrid(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _transcriptionResultsWindow?.Close();
        _transcriptionResultsCoordinator.State.PropertyChanged -= OnTranscriptionResultsPropertyChanged;
        _transcriptionResultsCoordinator.Dispose();
        _viewModel.Dispose();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void OnTitleBarCloseButtonClick(object sender, System.Windows.RoutedEventArgs e) => Close();
    private void OnSeekDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e) => _viewModel.BeginSeek();
    private void OnSeekDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e) => _viewModel.EndSeek();

    private void OnRecordingGridMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as System.Windows.DependencyObject;
        var row = FindParent<System.Windows.Controls.DataGridRow>(source);
        if (row is null) return;
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
        if (row is null) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void OpenTranscriptionResultsWindow()
    {
        if (_viewModel.SelectedItem is null || TranscriptionResults.Results.Count == 0) return;

        if (_transcriptionResultsWindow is { IsLoaded: true })
        {
            if (_transcriptionResultsWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _transcriptionResultsWindow.WindowState = System.Windows.WindowState.Normal;
            }
            _transcriptionResultsWindow.Activate();
            return;
        }

        _transcriptionResultsWindow = new TranscriptionResultsWindow(TranscriptionResults) { Owner = this };
        _transcriptionResultsWindow.Closed += (_, _) => _transcriptionResultsWindow = null;
        _transcriptionResultsWindow.Show();
    }

    private static IEnumerable<T> FindDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T matched) yield return matched;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static T? FindParent<T>(System.Windows.DependencyObject? child)
        where T : System.Windows.DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T matched) return matched;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
