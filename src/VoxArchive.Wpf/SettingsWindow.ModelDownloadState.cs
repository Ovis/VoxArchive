using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定WindowへWhisperモデル取得処理のアプリケーション共有状態を反映する
/// </summary>
public partial class SettingsWindow
{
    private Button? _modelDownloadButton;
    private Button? _modelDeleteButton;

    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // ダウンロードは設定Windowより長く生存するWhisperModelStoreで実行されるため、
        // Windowを開き直した時点でStoreの状態を読み直し、閉じる前のUI状態に依存しないようにする。
        _modelDownloadButton ??= FindButtonByContent(this, "モデル取得");
        _modelDeleteButton ??= FindButtonByContent(this, "モデル削除");
        _whisperModelStore.DownloadStateChanged -= OnWhisperModelDownloadStateChanged;
        _whisperModelStore.DownloadStateChanged += OnWhisperModelDownloadStateChanged;
        ModelComboBox.SelectionChanged -= OnModelSelectionChangedForDownloadState;
        ModelComboBox.SelectionChanged += OnModelSelectionChangedForDownloadState;
        RefreshModelDownloadUi();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _whisperModelStore.DownloadStateChanged -= OnWhisperModelDownloadStateChanged;
        ModelComboBox.SelectionChanged -= OnModelSelectionChangedForDownloadState;
        base.OnClosed(e);
    }

    private void OnModelSelectionChangedForDownloadState(object sender, SelectionChangedEventArgs e)
    {
        RefreshModelDownloadUi();
    }

    private void OnWhisperModelDownloadStateChanged(object? sender, WhisperModelDownloadStateChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshModelDownloadUi);
            return;
        }

        RefreshModelDownloadUi();
    }

    private void RefreshModelDownloadUi()
    {
        var model = TranscriptionModel;
        var isDownloading = _whisperModelStore.IsDownloading(model);

        if (_modelDownloadButton is not null)
        {
            _modelDownloadButton.IsEnabled = !isDownloading;
        }

        if (_modelDeleteButton is not null)
        {
            _modelDeleteButton.IsEnabled = !isDownloading;
        }

        // 元のWindowでも取得中はモデル選択を固定しているため、開き直したWindowでも同じ制約を維持する。
        ModelComboBox.IsEnabled = !isDownloading;

        if (isDownloading)
        {
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = "モデルをダウンロードしています...";
            return;
        }

        // 別のWindowで開始した取得が完了した場合も、現在のWindowだけで完了を確認できる表示へ戻す。
        if (_whisperModelStore.IsInstalled(model))
        {
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = $"モデル取得済み: {_whisperModelStore.GetModelPath(model)}";
        }
    }

    private static Button? FindButtonByContent(DependencyObject root, string content)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Button button && string.Equals(button.Content?.ToString(), content, StringComparison.Ordinal))
            {
                return button;
            }

            var nested = FindButtonByContent(child, content);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
