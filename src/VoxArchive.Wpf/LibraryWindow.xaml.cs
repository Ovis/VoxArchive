namespace VoxArchive.Wpf;

/// <summary>
/// 録音ライブラリの表示と、ウィンドウ固有のUIイベントを仲介する
/// </summary>
public partial class LibraryWindow : System.Windows.Window
{
    private readonly LibraryViewModel _viewModel;
    private readonly LibraryTranscriptionResultsCoordinator _transcriptionResultsCoordinator;
    private TranscriptionResultsWindow? _transcriptionResultsWindow;

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
            AttachTranscriptionResultsPanel();
        }
        catch
        {
            // Library本体は文字起こし結果が壊れていても利用できる必要があるため、起動失敗にはしない。
        }
    }

    /// <summary>
    /// 既存の編集領域へ文字起こし結果パネルと独立Windowを開く操作を追加する
    /// </summary>
    private void AttachTranscriptionResultsPanel()
    {
        var detailGrid = FindDetailGrid(this);
        if (detailGrid is null)
        {
            return;
        }

        var editContent = detailGrid.Children
            .OfType<System.Windows.UIElement>()
            .FirstOrDefault(x => System.Windows.Controls.Grid.GetRow(x) == 9 && System.Windows.Controls.Grid.GetRowSpan(x) == 1);
        if (editContent is null || editContent is System.Windows.Controls.ScrollViewer)
        {
            return;
        }

        // 既存ボタンは外部エディタを開く操作であることを明示し、その隣にアプリ内の独立Windowを追加する。
        // XAML全体のDataContext構成を変更せず、今回追加した結果UIだけを局所的に差し込むためここで構築する。
        var buttonRow = FindDescendants<System.Windows.Controls.StackPanel>(editContent)
            .FirstOrDefault(x => x.Orientation == System.Windows.Controls.Orientation.Horizontal &&
                                 x.Children.OfType<System.Windows.Controls.Button>().Any(b => Equals(b.Content, "文字起こし結果を開く")));
        var editorButton = buttonRow?.Children
            .OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(b => Equals(b.Content, "文字起こし結果を開く"));
        if (buttonRow is not null && editorButton is not null)
        {
            editorButton.Content = "文字起こし結果をエディタで開く";
            editorButton.Margin = new System.Windows.Thickness(0, 0, 8, 0);

            var detachedButton = new System.Windows.Controls.Button
            {
                Content = "文字起こし結果を新しいウィンドウで開く",
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };
            detachedButton.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "FlatButtonStyle");
            detachedButton.Click += OnOpenTranscriptionResultsWindowClick;
            buttonRow.Children.Insert(buttonRow.Children.IndexOf(editorButton) + 1, detachedButton);
        }

        detailGrid.Children.Remove(editContent);

        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(editContent);
        stack.Children.Add(new LibraryTranscriptionResultsPanel(TranscriptionResults));

        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };
        System.Windows.Controls.Grid.SetRow(scrollViewer, 9);
        detailGrid.Children.Add(scrollViewer);
    }

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
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _transcriptionResultsWindow?.Close();
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

    private void OnOpenTranscriptionResultsWindowClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is null)
        {
            return;
        }

        if (_transcriptionResultsWindow is { IsLoaded: true })
        {
            if (_transcriptionResultsWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _transcriptionResultsWindow.WindowState = System.Windows.WindowState.Normal;
            }

            _transcriptionResultsWindow.Activate();
            return;
        }

        // Libraryと同じStateを共有することで、録音や結果の選択変更を別Windowにも即時反映する。
        // WindowごとにJSONを再走査すると選択状態が競合するため、独立したStateは作らない。
        _transcriptionResultsWindow = new TranscriptionResultsWindow(TranscriptionResults)
        {
            Owner = this
        };
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
            if (child is T matched)
            {
                yield return matched;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
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
