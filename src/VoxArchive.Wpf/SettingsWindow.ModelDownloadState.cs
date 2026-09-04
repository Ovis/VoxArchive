using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Windowへ文字起こしモデル取得処理のアプリケーション共有状態を反映する
/// </summary>
public partial class SettingsWindow
{
    private const double DisabledActionOpacity = 0.55;

    private Button? _modelDownloadButton;
    private Button? _modelDeleteButton;
    private Button? _reazonSpeechModelButton;

    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // ダウンロードは設定Windowより長く生存するWhisperModelStoreで実行されるため、
        // Windowを開き直した時点でStoreの状態を読み直し、閉じる前のUI状態に依存しないようにする。
        _modelDownloadButton ??= FindButtonByContent(this, "モデル取得");
        _modelDeleteButton ??= FindButtonByContent(this, "モデル削除");
        EnsureReazonSpeechModelButton();
        EnsureTranscriptionEngineSelector();
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

    private void EnsureReazonSpeechModelButton()
    {
        if (_reazonSpeechModelButton is not null || _modelDeleteButton?.Parent is not WrapPanel actionPanel)
        {
            return;
        }

        // ReazonSpeechは複数ファイルモデルなので、現行Whisper用ボタンへ物理構成の差異を持ち込まず専用管理画面へ分離する。
        // 設定画面全体のモデル管理UIを再編するまでは、既に実機確認済みのProvider操作をこの入口から再利用する。
        _reazonSpeechModelButton = new Button
        {
            Content = "ReazonSpeechモデル",
            ToolTip = "ReazonSpeechモデルの取得・削除・完全性確認"
        };
        _reazonSpeechModelButton.Click += OnReazonSpeechModelClick;
        actionPanel.Children.Add(_reazonSpeechModelButton);
    }

    private void OnReazonSpeechModelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = System.Windows.Application.Current as App
                ?? throw new InvalidOperationException("VoxArchiveアプリケーションを取得できません。");
            var provider = app.Services.GetRequiredService<ReazonSpeechModelProvider>();
            var dialog = new ReazonSpeechModelWindow(provider)
            {
                Owner = this
            };
            _ = dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ModernDialog.Show(this, $"ReazonSpeechモデル管理を開けませんでした。{Environment.NewLine}{ex.Message}", "モデル管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        var isWhisper = string.Equals(DefaultTranscriptionEngine, TranscriptionEngineId.Whisper.Value, StringComparison.OrdinalIgnoreCase);
        if (!isWhisper)
        {
            // 既存の「モデル取得/削除」はWhisperModelStore専用なので、ReazonSpeech選択中に誤操作できないよう遮断する。
            // ReazonSpeech側は隣の専用モデル管理から共通PackageInstallerを利用する。
            ApplyActionAvailability(_modelDownloadButton, false);
            ApplyActionAvailability(_modelDeleteButton, false);
            UpdateEngineSpecificUi();
            return;
        }

        var model = TranscriptionModel;
        var isDownloading = _whisperModelStore.IsDownloading(model);

        ApplyActionAvailability(_modelDownloadButton, !isDownloading);
        ApplyActionAvailability(_modelDeleteButton, !isDownloading);

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

    private static void ApplyActionAvailability(Button? button, bool isAvailable)
    {
        if (button is null)
        {
            return;
        }

        // WPF標準Buttonの無効状態はOSテーマの明色へ置き換わり、設定画面のダークテーマから浮いてしまう。
        // 既存のダーク配色を維持したまま他の非活性コントロールと同程度に減光し、入力経路もすべて遮断する。
        button.IsHitTestVisible = isAvailable;
        button.Focusable = isAvailable;
        button.IsTabStop = isAvailable;
        button.Opacity = isAvailable ? 1d : DisabledActionOpacity;
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
