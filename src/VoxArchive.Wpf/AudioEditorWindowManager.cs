using System.IO;
using System.Windows;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editor Windowの起動単位をアプリ全体で管理する。
/// </summary>
/// <remarks>
/// 同じ元音声に対して複数Editorを開くと、別々の編集セッションが同一ファイルを基準に進み利用者が混乱するため、
/// 元音声パスごとに最大1Windowとする。異なる元音声のEditorは同時に開ける。
/// </remarks>
public static class AudioEditorWindowManager
{
    private static readonly Dictionary<string, AudioEditorWindow> Windows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 指定録音のEditorを開く。既に開いている場合は既存Windowを前面へ出す。
    /// </summary>
    public static void Open(Window owner, LibraryRecordingItem item)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(item);

        if (!File.Exists(item.FilePath))
        {
            ModernDialog.Show(owner, "元音声ファイルが見つかりません。", "音声編集", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(Path.GetExtension(item.FilePath), ".flac", StringComparison.OrdinalIgnoreCase))
        {
            ModernDialog.Show(owner, "音声編集の入力はFLACファイルのみ対応しています。", "音声編集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = Path.GetFullPath(item.FilePath);
        if (Windows.TryGetValue(key, out var existing) && existing.IsLoaded)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Activate();
            return;
        }

        var window = new AudioEditorWindow(item) { Owner = owner };
        Windows[key] = window;
        window.Closed += (_, _) => Windows.Remove(key);
        window.Show();
    }
}