using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VoxArchive.Wpf;

public partial class ModernDialogWindow : Window
{
    public ModernDialogWindow(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        InitializeComponent();

        TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "確認" : title;
        MessageTextBlock.Text = message;

        ConfigureIcon(image);
        ConfigureButtons(buttons, defaultResult);

        PreviewKeyDown += OnPreviewKeyDown;
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private void ConfigureIcon(MessageBoxImage image)
    {
        switch (image)
        {
            case MessageBoxImage.Warning:
                IconTextBlock.Text = "!";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 202, 77));
                break;
            case MessageBoxImage.Error:
                IconTextBlock.Text = "x";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 117, 117));
                break;
            case MessageBoxImage.Question:
                IconTextBlock.Text = "?";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(150, 201, 255));
                break;
            default:
                IconTextBlock.Text = "i";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(175, 198, 233));
                break;
        }
    }

    private void ConfigureButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                PrimaryButton.Content = "OK";
                SecondaryButton.Visibility = Visibility.Collapsed;
                SetDefaultButton(defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult);
                break;

            case MessageBoxButton.OKCancel:
                PrimaryButton.Content = "OK";
                SecondaryButton.Content = "キャンセル";
                SecondaryButton.Visibility = Visibility.Visible;
                SetDefaultButton(defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult);
                break;

            case MessageBoxButton.YesNo:
                PrimaryButton.Content = "はい";
                SecondaryButton.Content = "いいえ";
                SecondaryButton.Visibility = Visibility.Visible;
                SetDefaultButton(defaultResult == MessageBoxResult.None ? MessageBoxResult.Yes : defaultResult);
                break;

            default:
                PrimaryButton.Content = "OK";
                SecondaryButton.Visibility = Visibility.Collapsed;
                SetDefaultButton(MessageBoxResult.OK);
                break;
        }
    }

    private void SetDefaultButton(MessageBoxResult defaultResult)
    {
        var isPrimaryDefault = defaultResult is MessageBoxResult.OK or MessageBoxResult.Yes;
        if (isPrimaryDefault)
        {
            PrimaryButton.IsDefault = true;
            SecondaryButton.IsCancel = true;
            PrimaryButton.Focus();
        }
        else
        {
            PrimaryButton.IsDefault = false;
            SecondaryButton.IsCancel = true;
            SecondaryButton.Focus();
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Result = PrimaryButton.Content?.ToString() == "はい" ? MessageBoxResult.Yes : MessageBoxResult.OK;
        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Result = SecondaryButton.Content?.ToString() switch
        {
            "いいえ" => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (SecondaryButton.Visibility == Visibility.Visible)
        {
            Result = SecondaryButton.Content?.ToString() switch
            {
                "いいえ" => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };
            DialogResult = false;
        }
        else
        {
            Result = MessageBoxResult.OK;
            DialogResult = true;
        }

        e.Handled = true;
    }
}

