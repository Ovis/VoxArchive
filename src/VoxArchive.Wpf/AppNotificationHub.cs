using System.Windows.Forms;

namespace VoxArchive.Wpf;

public static class AppNotificationHub
{
    public static event Action<string, string, ToolTipIcon>? BalloonRequested;

    public static void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        BalloonRequested?.Invoke(title, message, icon);
    }
}
