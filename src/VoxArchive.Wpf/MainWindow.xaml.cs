using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoxArchive.Wpf;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private KeyBinding? _startStopKeyBinding;

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
        ApplyStartStopHotkeyBinding();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.WindowWidth) or nameof(MainViewModel.WindowHeight))
        {
            ApplyWindowSize();
        }

        if (e.PropertyName == nameof(MainViewModel.StartStopHotkeyText))
        {
            ApplyStartStopHotkeyBinding();
        }
    }

    private void OnDeviceListBoxPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var origin = e.OriginalSource as DependencyObject;
        var listBoxItem = FindAncestor<ListBoxItem>(origin);
        if (listBoxItem is null)
        {
            return;
        }

        _viewModel.IsSpeakerDevicePopupOpenNormal = false;
        _viewModel.IsSpeakerDevicePopupOpenMini = false;
        _viewModel.IsMicDevicePopupOpenNormal = false;
        _viewModel.IsMicDevicePopupOpenMini = false;
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T result)
            {
                return result;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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

    private void ApplyStartStopHotkeyBinding()
    {
        if (_startStopKeyBinding is not null)
        {
            InputBindings.Remove(_startStopKeyBinding);
            _startStopKeyBinding = null;
        }

        if (_viewModel is null)
        {
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(_viewModel.StartStopHotkeyText, out var gesture, out _)
            || gesture is null)
        {
            return;
        }

        _startStopKeyBinding = new KeyBinding(_viewModel.StartStopCommand, gesture);
        InputBindings.Add(_startStopKeyBinding);
    }
}
