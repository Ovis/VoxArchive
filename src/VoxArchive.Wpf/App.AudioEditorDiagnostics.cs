using System.IO;
using System.Text;
using System.Windows.Threading;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editor起動クラッシュ調査中だけ有効にする一時診断処理。
/// </summary>
/// <remarks>
/// 通常のZLoggerはプロセスが異常終了した場合に末尾がflushされない可能性があるため、
/// UIスレッド未処理例外だけは同期追記の専用ログにも残す。原因特定後、このファイル自体を削除する。
/// </remarks>
public partial class App
{
    private static readonly object AudioEditorDiagnosticLogGate = new();

    static App()
    {
        Dispatcher.CurrentDispatcher.UnhandledException += OnAudioEditorDiagnosticDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAudioEditorDiagnosticDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnAudioEditorDiagnosticUnobservedTaskException;
        WriteAudioEditorDiagnostic("Diagnostic hooks installed.");
    }

    internal static void WriteAudioEditorDiagnostic(string message, Exception? exception = null)
    {
        try
        {
            var logsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxArchive",
                "logs");
            Directory.CreateDirectory(logsDirectory);
            var path = Path.Combine(logsDirectory, "audio-editor-diagnostic.log");
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [")
                .Append(Environment.ProcessId)
                .Append(':')
                .Append(Environment.CurrentManagedThreadId)
                .Append("] ")
                .AppendLine(message);
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (AudioEditorDiagnosticLogGate)
            {
                File.AppendAllText(path, builder.ToString(), System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 診断ログ自身の失敗で本来の例外経路を変えない。
        }
    }

    private static void OnAudioEditorDiagnosticDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => WriteAudioEditorDiagnostic("DispatcherUnhandledException captured. The exception remains unhandled.", e.Exception);

    private static void OnAudioEditorDiagnosticDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => WriteAudioEditorDiagnostic(
            $"AppDomain.UnhandledException captured. IsTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception);

    private static void OnAudioEditorDiagnosticUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => WriteAudioEditorDiagnostic("TaskScheduler.UnobservedTaskException captured.", e.Exception);
}
