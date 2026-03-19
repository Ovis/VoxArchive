using System.Windows;

namespace VoxArchive.Wpf;

public static class ModernDialog
{
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        return Show(null, message, title, buttons, image, defaultResult);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new ModernDialogWindow(message, title, buttons, image, defaultResult)
        {
            Owner = ResolveOwner(owner)
        };

        dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? ResolveOwner(Window? owner)
    {
        if (owner is not null)
        {
            return owner;
        }

        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return active ?? app.MainWindow;
    }
}

