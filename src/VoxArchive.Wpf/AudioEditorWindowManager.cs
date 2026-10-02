using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Domain;

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

        // VoxArchive.Application 名前空間との名前解決競合を避けるため、WPF Application を完全修飾する。
        var app = (App)System.Windows.Application.Current;
        var exportCoordinator = app.Services.GetRequiredService<AudioExportCoordinator>();
        var runtimeHolder = app.Services.GetRequiredService<RecordingRuntimeContextHolder>();
        var recordingState = runtimeHolder.Context?.RecordingService.CurrentState ?? RecordingState.Stopped;
        var unavailableReason = AudioEditorAvailability.GetUnavailableReason(recordingState, exportCoordinator.IsExporting);
        if (unavailableReason is not null)
        {
            ModernDialog.Show(owner, unavailableReason, "音声編集", MessageBoxButton.OK, MessageBoxImage.Information);
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

        var playback = app.Services.GetRequiredService<IRecordingPlaybackService>();
        var catalog = app.Services.GetRequiredService<RecordingCatalogService>();
        var window = new AudioEditorWindow(item, playback, exportCoordinator, catalog) { Owner = owner };
        Windows[key] = window;
        window.Closed += (_, _) => Windows.Remove(key);
        window.Show();
    }
}
