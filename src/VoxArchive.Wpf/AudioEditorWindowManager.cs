using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // 起動直後のクラッシュ原因切り分け用の一時診断ログ。
        // 原因特定後、この詳細ログはPR内で削除する。
        var app = (App)System.Windows.Application.Current;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("AudioEditorLaunch");
        var requestDetail = $"File={item.FilePath}, Exists={File.Exists(item.FilePath)}, Extension={Path.GetExtension(item.FilePath)}, Channels={item.Channels}, SampleRate={item.SampleRate}, DurationMs={item.DurationMilliseconds}";
        logger.LogInformation("Audio Editor open requested. {RequestDetail}", requestDetail);
        App.WriteAudioEditorDiagnostic($"Audio Editor open requested. {requestDetail}");

        try
        {
            if (!File.Exists(item.FilePath))
            {
                logger.LogWarning("Audio Editor open rejected because source file does not exist. File={FilePath}", item.FilePath);
                App.WriteAudioEditorDiagnostic($"Audio Editor open rejected: source file missing. File={item.FilePath}");
                ModernDialog.Show(owner, "元音声ファイルが見つかりません。", "音声編集", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.Equals(Path.GetExtension(item.FilePath), ".flac", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Audio Editor open rejected because source is not FLAC. File={FilePath}", item.FilePath);
                App.WriteAudioEditorDiagnostic($"Audio Editor open rejected: source is not FLAC. File={item.FilePath}");
                ModernDialog.Show(owner, "音声編集の入力はFLACファイルのみ対応しています。", "音声編集", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            logger.LogInformation("Resolving Audio Editor application services.");
            App.WriteAudioEditorDiagnostic("Resolving Audio Editor application services.");
            var exportCoordinator = app.Services.GetRequiredService<AudioExportCoordinator>();
            var runtimeHolder = app.Services.GetRequiredService<RecordingRuntimeContextHolder>();
            var recordingState = runtimeHolder.Context?.RecordingService.CurrentState ?? RecordingState.Stopped;
            logger.LogInformation(
                "Audio Editor availability check. RecordingState={RecordingState}, IsExporting={IsExporting}, RuntimeContextAvailable={RuntimeContextAvailable}",
                recordingState,
                exportCoordinator.IsExporting,
                runtimeHolder.Context is not null);
            App.WriteAudioEditorDiagnostic($"Availability check. RecordingState={recordingState}, IsExporting={exportCoordinator.IsExporting}, RuntimeContextAvailable={runtimeHolder.Context is not null}");

            var unavailableReason = AudioEditorAvailability.GetUnavailableReason(recordingState, exportCoordinator.IsExporting);
            if (unavailableReason is not null)
            {
                logger.LogInformation("Audio Editor open rejected by availability guard. Reason={Reason}", unavailableReason);
                App.WriteAudioEditorDiagnostic($"Audio Editor open rejected by availability guard. Reason={unavailableReason}");
                ModernDialog.Show(owner, unavailableReason, "音声編集", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var key = Path.GetFullPath(item.FilePath);
            logger.LogInformation("Audio Editor normalized source path. Key={Key}, ExistingWindowCount={WindowCount}", key, Windows.Count);
            App.WriteAudioEditorDiagnostic($"Normalized source path. Key={key}, ExistingWindowCount={Windows.Count}");
            if (Windows.TryGetValue(key, out var existing) && existing.IsLoaded)
            {
                logger.LogInformation("Existing Audio Editor found. Activating existing window. Key={Key}", key);
                App.WriteAudioEditorDiagnostic($"Existing Audio Editor found. Activating. Key={key}");
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }
                existing.Activate();
                return;
            }

            logger.LogInformation("Resolving playback/catalog services for new Audio Editor.");
            App.WriteAudioEditorDiagnostic("Resolving playback/catalog services for new Audio Editor.");
            var playback = app.Services.GetRequiredService<IRecordingPlaybackService>();
            var catalog = app.Services.GetRequiredService<RecordingCatalogService>();

            logger.LogInformation("Constructing AudioEditorWindow. Key={Key}", key);
            App.WriteAudioEditorDiagnostic($"Constructing AudioEditorWindow. Key={key}");
            var window = new AudioEditorWindow(item, playback, exportCoordinator, catalog) { Owner = owner };
            logger.LogInformation("AudioEditorWindow constructed. Key={Key}, IsLoaded={IsLoaded}", key, window.IsLoaded);
            App.WriteAudioEditorDiagnostic($"AudioEditorWindow constructed. Key={key}, IsLoaded={window.IsLoaded}");

            Windows[key] = window;
            window.Closed += (_, _) =>
            {
                logger.LogInformation("AudioEditorWindow closed. Removing window registry entry. Key={Key}", key);
                App.WriteAudioEditorDiagnostic($"AudioEditorWindow closed. Key={key}");
                Windows.Remove(key);
            };

            logger.LogInformation("Calling AudioEditorWindow.Show(). Key={Key}", key);
            App.WriteAudioEditorDiagnostic($"Calling AudioEditorWindow.Show(). Key={key}");
            window.Show();
            logger.LogInformation("AudioEditorWindow.Show() returned. Key={Key}, IsLoaded={IsLoaded}, IsVisible={IsVisible}", key, window.IsLoaded, window.IsVisible);
            App.WriteAudioEditorDiagnostic($"AudioEditorWindow.Show() returned. Key={key}, IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Audio Editor open path threw an unhandled exception. File={FilePath}", item.FilePath);
            App.WriteAudioEditorDiagnostic($"Audio Editor open path threw. File={item.FilePath}", ex);
            throw;
        }
    }
}
