namespace VoxArchive.Wpf;

public partial class LibraryWindow : System.Windows.Window
{
    private readonly LibraryViewModel _viewModel;

    public LibraryWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
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
