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

    private void OnSeekDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.BeginSeek();
    }

    private void OnSeekDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.EndSeek();
    }
}
