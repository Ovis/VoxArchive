using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace VoxArchive.Wpf;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int StartStopHotkeyId = 0x3001;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private MainViewModel? _viewModel;
    private HwndSource? _hwndSource;
    private bool _isStartStopHotkeyRegistered;
    private bool _isExitRequested;
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayAppIcon;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();

        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        var showMainItem = new Forms.ToolStripMenuItem("メイン画面を表示");
        showMainItem.Click += (_, _) => ShowFromTray();

        var showLibraryItem = new Forms.ToolStripMenuItem("ライブラリを表示");
        showLibraryItem.Click += (_, _) => OpenLibraryFromTray();

        var exitItem = new Forms.ToolStripMenuItem("終了");
        exitItem.Click += (_, _) => ExitFromTray();

        menu.Items.Add(showMainItem);
        menu.Items.Add(showLibraryItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayAppIcon = LoadTrayApplicationIcon();

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "VoxArchive",
            Icon = _trayAppIcon ?? Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Drawing.Icon? LoadTrayApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return null;
            }

            return Drawing.Icon.ExtractAssociatedIcon(processPath)?.Clone() as Drawing.Icon;
        }
        catch
        {
            return null;
        }
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(WndProc);
        UpdateGlobalStartStopHotkey();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        UnregisterGlobalStartStopHotkey();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _trayAppIcon?.Dispose();
        _trayAppIcon = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
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
        UpdateGlobalStartStopHotkey();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.WindowWidth) or nameof(MainViewModel.WindowHeight))
        {
            ApplyWindowSize();
        }

        if (e.PropertyName == nameof(MainViewModel.StartStopHotkeyText))
        {
            UpdateGlobalStartStopHotkey();
        }
    }

    private void OpenLibraryFromTray()
    {
        ShowFromTray();

        if (_viewModel?.OpenLibraryCommand.CanExecute(null) == true)
        {
            _viewModel.OpenLibraryCommand.Execute(null);
        }
    }

    private void ExitFromTray()
    {
        _isExitRequested = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        Close();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnTitleBarCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
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
        _viewModel.IsMicDevicePopupOpenNormal = false;
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

    private void UpdateGlobalStartStopHotkey()
    {
        UnregisterGlobalStartStopHotkey();

        if (_viewModel is null || _hwndSource is null)
        {
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(_viewModel.StartStopHotkeyText, out var gesture, out var normalized)
            || gesture is null)
        {
            return;
        }

        var modifiers = ToNativeModifiers(gesture.Modifiers) | ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (virtualKey == 0)
        {
            return;
        }

        if (!RegisterHotKey(_hwndSource.Handle, StartStopHotkeyId, modifiers, virtualKey))
        {
            ModernDialog.Show(
                this,
                $"ショートカット '{normalized}' を登録できませんでした。\n他アプリで使用中の可能性があります。",
                "ホットキー登録失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _isStartStopHotkeyRegistered = true;
    }

    private void UnregisterGlobalStartStopHotkey()
    {
        if (!_isStartStopHotkeyRegistered || _hwndSource is null)
        {
            return;
        }

        _ = UnregisterHotKey(_hwndSource.Handle, StartStopHotkeyId);
        _isStartStopHotkeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == StartStopHotkeyId)
        {
            if (_viewModel?.StartStopCommand.CanExecute(null) == true)
            {
                _viewModel.StartStopCommand.Execute(null);
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var native = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            native |= ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            native |= ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            native |= ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            native |= ModWin;
        }

        return native;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

