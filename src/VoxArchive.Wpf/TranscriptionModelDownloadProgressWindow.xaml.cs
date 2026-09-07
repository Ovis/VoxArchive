using System.Windows;
using System.Windows.Input;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// 進行中の文字起こしモデル取得を表示し、ユーザーからの取得キャンセル操作を受け付ける
/// </summary>
/// <remarks>
/// Window自身はモデル取得を所有せず、WPFが呼び出すApplication Use Caseの進捗だけを表示する。
/// Windowを閉じても取得は継続し、明示的なキャンセル操作だけをApplication Facadeへ委譲する。
/// </remarks>
public partial class TranscriptionModelDownloadProgressWindow : Window
{
    private readonly Action _cancelAction;

    /// <summary>モデル取得進捗Windowを初期化する</summary>
    public TranscriptionModelDownloadProgressWindow(
        TranscriptionMissingModelInfo model,
        Action cancelAction)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cancelAction);

        _cancelAction = cancelAction;

        InitializeComponent();
        ModelNameTextBlock.Text = $"{GetEngineDisplayName(model.EngineId)} / {model.DisplayName}";
        ProgressTextBlock.Text = "モデル取得を開始しています...";
    }

    /// <summary>取得進捗を表示へ反映する</summary>
    public void Report(TranscriptionModelTransferInfo progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Report(progress));
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        ProgressTextBlock.Text = progress.TotalBytes > 0
            ? $"{progress.Percent:F0}%（{FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.TotalBytes)}）"
            : $"{FormatBytes(progress.BytesReceived)} 取得済み";
        DownloadProgressBar.Value = progress.Percent;
    }

    /// <summary>モデル取得完了後にWindowを閉じる</summary>
    public void CloseAfterCompletion()
    {
        if (!Dispatcher.CheckAccess())
        {
            // Application側のfinally直後にDisposeされてもWindowだけ残らないよう、
            // 完了時のCloseはUIスレッドで同期的に完了させる。
            Dispatcher.Invoke(CloseAfterCompletion);
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        Close();
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        // キャンセルの実体はApplication側が所有する。
        // Window側では二重操作を防ぐため表示だけを先に無効化する。
        CancelDownloadButton.IsEnabled = false;
        CancelDownloadButton.Content = "取得をキャンセル中";
        _cancelAction();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // ×は進捗表示だけを閉じる。モデル取得まで止めるとWindow寿命が処理寿命を所有してしまうため、
        // 取得停止は明示的なキャンセルボタンだけで行う。
        Close();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private static string GetEngineDisplayName(string engineId)
    {
        return engineId.Equals("reazonspeech", StringComparison.OrdinalIgnoreCase)
            ? "ReazonSpeech"
            : engineId.Equals("whisper", StringComparison.OrdinalIgnoreCase)
                ? "Whisper"
                : engineId;
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, value);
        var unitIndex = 0;
        double display = size;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{display:F0} {units[unitIndex]}"
            : $"{display:F1} {units[unitIndex]}";
    }
}
