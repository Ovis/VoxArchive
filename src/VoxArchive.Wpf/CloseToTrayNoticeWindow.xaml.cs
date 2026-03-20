using System.Windows;

namespace VoxArchive.Wpf;

public partial class CloseToTrayNoticeWindow : Window
{
    public CloseToTrayNoticeWindow()
    {
        InitializeComponent();
    }

    public bool SuppressFutureNotice => DontShowAgainCheckBox.IsChecked == true;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
