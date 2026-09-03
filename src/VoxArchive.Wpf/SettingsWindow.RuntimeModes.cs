using System.Windows;
using System.Windows.Controls;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public partial class SettingsWindow
{
    static SettingsWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(SettingsWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnSettingsWindowLoaded));
    }

    private static void OnSettingsWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsWindow window)
        {
            return;
        }

        // CudaPreferred existed before Whisper.net 1.9.1 runtime auto-selection was adopted.
        // Keep the enum value deserializable, but do not expose it as a selectable mode.
        var legacyItems = window.ExecutionModeComboBox.Items
            .OfType<ComboBoxItem>()
            .Where(item => item.Tag is TranscriptionExecutionMode mode && mode == TranscriptionExecutionMode.CudaPreferred)
            .ToArray();

        foreach (var item in legacyItems)
        {
            window.ExecutionModeComboBox.Items.Remove(item);
        }

        if (window.ExecutionModeComboBox.SelectedItem is null)
        {
            window.TranscriptionExecutionMode = TranscriptionExecutionMode.Auto;
        }
    }
}
