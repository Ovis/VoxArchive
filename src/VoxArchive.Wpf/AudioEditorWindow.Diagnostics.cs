using System.Windows;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editor起動クラッシュ調査中だけ有効にするWindow到達点ログ。
/// </summary>
/// <remarks>
/// Constructor完了後にWPFのLoaded経路まで到達したかを判定するための一時コード。
/// 原因特定後、このファイル自体を削除する。
/// </remarks>
public partial class AudioEditorWindow
{
    static AudioEditorWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(AudioEditorWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDiagnosticLoaded));
        EventManager.RegisterClassHandler(
            typeof(AudioEditorWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnDiagnosticUnloaded));
    }

    private static void OnDiagnosticLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AudioEditorWindow window) return;
        App.WriteAudioEditorDiagnostic(
            $"AudioEditorWindow Loaded routed event reached. Source={SafeSourcePath(window)}, IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}");
    }

    private static void OnDiagnosticUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AudioEditorWindow window) return;
        App.WriteAudioEditorDiagnostic(
            $"AudioEditorWindow Unloaded routed event reached. Source={SafeSourcePath(window)}, IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}");
    }

    private static string SafeSourcePath(AudioEditorWindow window)
    {
        try
        {
            return window.SourceFilePath;
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }
}
