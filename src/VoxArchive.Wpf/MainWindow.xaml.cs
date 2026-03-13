using System.ComponentModel;
using System.Windows;

namespace VoxArchive.Wpf;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyWindowSize();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.WindowWidth) or nameof(MainViewModel.WindowHeight))
        {
            ApplyWindowSize();
        }
    }

    private void ApplyWindowSize()
    {
        if (_viewModel is null)
        {
            return;
        }

        Width = _viewModel.WindowWidth;
        Height = _viewModel.WindowHeight;
        MinWidth = _viewModel.WindowWidth;
        MinHeight = _viewModel.WindowHeight;
        MaxWidth = _viewModel.WindowWidth;
        MaxHeight = _viewModel.WindowHeight;
    }
}
