using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 進行中の文字起こしモデル取得を表示し、ユーザーからの取得キャンセル操作を受け付ける
/// </summary>
/// <remarks>
/// Window自身はダウンロード処理を所有せず、アプリケーション共有の
/// <see cref="TranscriptionModelManager"/> が公開する状態を表示するだけとする。
/// Windowを閉じても取得は継続し、明示的なキャンセルボタンだけが取得または待機を中断する。
/// </remarks>
public partial class TranscriptionModelDownloadProgressWindow : Window
{
    private readonly TranscriptionModelManager _modelManager;
    private readonly TranscriptionEngineId _engineId;
    private readonly TranscriptionModelId _modelId;
    private readonly bool _ownsDownload;
    private readonly Action _cancelAction;
    private bool _closeRequestedByCompletion;

    /// <summary>モデル取得進捗Windowを初期化する</summary>
    public TranscriptionModelDownloadProgressWindow(
        TranscriptionModelManager modelManager,
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        bool ownsDownload,
        Action cancelAction)
    {
        _modelManager = modelManager;
        _engineId = engineId;
        _modelId = modelId;
        _ownsDownload = ownsDownload;
        _cancelAction = cancelAction;

        InitializeComponent();
        _modelManager.StateChanged += OnModelManagerStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        RefreshState();
    }

    /// <summary>取得完了後にWindowを閉じる</summary>
    public void CloseAfterCompletion()
    {
        if (!IsVisible)
        {
            return;
        }

        _closeRequestedByCompletion = true;
        Close();
    }

    private void OnModelManagerStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshState);
            return;
        }

        RefreshState();
    }

    private void RefreshState()
    {
        var active = _modelManager.GetActiveDownload();
        if (active is null || active.EngineId != _engineId || active.ModelId != _modelId)
        {
            ProgressTextBlock.Text = "取得処理を終了しています...";
            DownloadProgressBar.Value = 100;
            CancelDownloadButton.IsEnabled = false;
            return;
        }

        ModelNameTextBlock.Text = $"{GetEngineDisplayName(active.EngineId)} / {active.ModelDisplayName}";
        ProgressTextBlock.Text = active.TotalBytes > 0
            ? $"{active.Percent:F0}%（{FormatBytes(active.BytesReceived)} / {FormatBytes(active.TotalBytes)}）"
            : $"{FormatBytes(active.BytesReceived)} 取得済み";
        DownloadProgressBar.Value = active.Percent;
        CancelDownloadButton.Content = _ownsDownload ? "取得をキャンセル" : "待機をキャンセル";
        CancelDownloadButton.IsEnabled = !active.IsCancelling;
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        _cancelAction();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // ユーザーが×で閉じる操作は進捗表示だけを閉じる。取得や文字起こし要求は継続する。
        // 完了処理から閉じる場合も同じく追加の副作用は持たせない。
        _ = _closeRequestedByCompletion;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _modelManager.StateChanged -= OnModelManagerStateChanged;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private static string GetEngineDisplayName(TranscriptionEngineId engineId)
        => engineId == TranscriptionEngineId.ReazonSpeech ? "ReazonSpeech" : "Whisper";

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

        return unitIndex == 0 ? $"{display:F0} {units[unitIndex]}" : $"{display:F1} {units[unitIndex]}";
    }
}
