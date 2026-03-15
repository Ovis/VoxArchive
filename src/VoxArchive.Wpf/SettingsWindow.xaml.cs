using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace VoxArchive.Wpf;

public partial class SettingsWindow : Window
{
    private bool _isCapturingHotkey;
    private string _capturedHotkeyText = string.Empty;

    public SettingsWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    public int AlignmentMilliseconds
    {
        get => int.TryParse(OffsetTextBox.Text, out var ms) ? ms : 0;
        set => OffsetTextBox.Text = value.ToString();
    }

    public double DefaultSpeakerPlaybackGainDb
    {
        get => ParseDouble(DefaultSpeakerGainTextBox.Text);
        set => DefaultSpeakerGainTextBox.Text = value.ToString("F1", CultureInfo.CurrentCulture);
    }

    public double DefaultMicPlaybackGainDb
    {
        get => ParseDouble(DefaultMicGainTextBox.Text);
        set => DefaultMicGainTextBox.Text = value.ToString("F1", CultureInfo.CurrentCulture);
    }

    public string StartStopHotkeyText
    {
        get => StartStopHotkeyTextBox.Text.Trim();
        set
        {
            StartStopHotkeyTextBox.Text = value;
            _capturedHotkeyText = value;
        }
    }

    public string OutputDirectory
    {
        get => OutputDirectoryTextBox.Text.Trim();
        set => OutputDirectoryTextBox.Text = value;
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

    private void OnToggleHotkeyCaptureClick(object sender, RoutedEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            _isCapturingHotkey = true;
            _capturedHotkeyText = StartStopHotkeyText;
            HotkeyCaptureButton.Content = "確定";
            StartStopHotkeyTextBox.Text = "キー入力待ち...";
            Keyboard.Focus(this);
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(_capturedHotkeyText, out _, out var normalizedHotkey))
        {
            ModernDialog.Show(this, "その組み合わせはショートカットとして利用できません。別のキーを指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isCapturingHotkey = false;
        HotkeyCaptureButton.Content = "キー設定";
        StartStopHotkeyText = normalizedHotkey;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isCapturingHotkey = false;
            HotkeyCaptureButton.Content = "キー設定";
            StartStopHotkeyTextBox.Text = _capturedHotkeyText;
            e.Handled = true;
            return;
        }

        if (KeyboardShortcutHelper.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (KeyboardShortcutHelper.TryBuildFromInput(Keyboard.Modifiers, key, out var normalizedHotkey))
        {
            _capturedHotkeyText = normalizedHotkey;
            StartStopHotkeyTextBox.Text = normalizedHotkey;
        }
        else
        {
            StartStopHotkeyTextBox.Text = "未対応の組み合わせです";
        }

        e.Handled = true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(OffsetTextBox.Text, out var offsetMs))
        {
            ModernDialog.Show(this, "マイク遅延補正は整数で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (offsetMs < -1000 || offsetMs > 1000)
        {
            ModernDialog.Show(this, "マイク遅延補正は -1000 ～ 1000 の範囲で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseGain(DefaultSpeakerGainTextBox.Text, out var speakerGain))
        {
            ModernDialog.Show(this, "既定 Speaker 再生ゲインは数値で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseGain(DefaultMicGainTextBox.Text, out var micGain))
        {
            ModernDialog.Show(this, "既定 Mic 再生ゲインは数値で入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (speakerGain < -60d || speakerGain > 48d || micGain < -60d || micGain > 48d)
        {
            ModernDialog.Show(this, "再生ゲインは -60dB ～ 48dB の範囲で指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DefaultSpeakerPlaybackGainDb = speakerGain;
        DefaultMicPlaybackGainDb = micGain;

        if (_isCapturingHotkey)
        {
            ModernDialog.Show(this, "ショートカット設定中です。キー設定ボタンをもう一度押して確定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!KeyboardShortcutHelper.TryParseAndNormalize(StartStopHotkeyText, out _, out var normalizedHotkey))
        {
            ModernDialog.Show(this, "ショートカットは F12 や Ctrl+F12 のように指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartStopHotkeyText = normalizedHotkey;

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            ModernDialog.Show(this, "保存先を指定してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static bool TryParseGain(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double ParseDouble(string text)
    {
        return TryParseGain(text, out var value) ? value : 0d;
    }
}
