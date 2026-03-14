using System.IO;
using System.Windows;

namespace VoxArchive.Wpf;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public int AlignmentMilliseconds
    {
        get => int.TryParse(OffsetTextBox.Text, out var ms) ? ms : 0;
        set => OffsetTextBox.Text = value.ToString();
    }

    public string OutputDirectory
    {
        get => OutputDirectoryTextBox.Text.Trim();
        set => OutputDirectoryTextBox.Text = value;
    }

    private void OnBrowseOutputDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "録音ファイルの保存先を選択",
            InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : null
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(OffsetTextBox.Text, out var offsetMs))
        {
            MessageBox.Show(this, "マイク遅延補正は整数で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (offsetMs < -1000 || offsetMs > 1000)
        {
            MessageBox.Show(this, "マイク遅延補正は -1000 ～ 1000 の範囲で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            MessageBox.Show(this, "保存先を指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}

