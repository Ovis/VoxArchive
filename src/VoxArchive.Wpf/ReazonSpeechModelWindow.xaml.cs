using System.IO;
using System.Windows;
using System.Windows.Media;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// ReazonSpeechモデルの取得・削除・完全性確認を設定画面から操作するWindowを提供する
/// </summary>
public partial class ReazonSpeechModelWindow : Window
{
    private static readonly Brush StatusDefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9BB4D1"));
    private static readonly Brush StatusErrorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9A9A"));

    private readonly ReazonSpeechModelProvider _modelProvider;
    private readonly TranscriptionModelDefinition _definition;
    private bool _isBusy;

    /// <summary>
    /// ReazonSpeechモデル管理Windowを初期化する
    /// </summary>
    /// <param name="modelProvider">アプリケーションと共有するReazonSpeechモデルProvider</param>
    public ReazonSpeechModelWindow(ReazonSpeechModelProvider modelProvider)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        _modelProvider = modelProvider;
        _definition = _modelProvider.GetDefinition(ReazonSpeechModelCatalog.JapaneseModelId);

        InitializeComponent();
        ModelNameTextBlock.Text = _definition.DisplayName;
        ModelDetailTextBlock.Text = $"Engine: {_definition.EngineId.Value} / Model: {_definition.ModelId.Value} / Version: {_definition.ArtifactVersion}";
        ModelSizeTextBlock.Text = $"取得サイズ: {FormatBytes(_definition.Files.Sum(file => file.Size))} / {_definition.Files.Count} ファイル / License: {_definition.License}";
        RefreshState();
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusyState(true, "モデルを取得しています。完了するまでこの画面を閉じないでください...");
        try
        {
            var installation = await _modelProvider.InstallAsync(_definition.ModelId);
            StatusTextBlock.Foreground = StatusDefaultBrush;
            StatusTextBlock.Text = $"モデル取得完了: {Path.GetDirectoryName(installation.PrimaryFile)}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Foreground = StatusErrorBrush;
            StatusTextBlock.Text = $"モデル取得失敗: {ex.Message}";
        }
        finally
        {
            SetBusyState(false, null);
            RefreshState(preserveStatus: true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (MessageBox.Show(this, "取得済みのReazonSpeechモデルを削除しますか？", "モデル削除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusyState(true, "モデルを削除しています...");
        try
        {
            await _modelProvider.DeleteAsync(_definition.ModelId);
            StatusTextBlock.Foreground = StatusDefaultBrush;
            StatusTextBlock.Text = "モデルを削除しました。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Foreground = StatusErrorBrush;
            StatusTextBlock.Text = $"モデル削除失敗: {ex.Message}";
        }
        finally
        {
            SetBusyState(false, null);
            RefreshState(preserveStatus: true);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshState();
    }

    private void RefreshState(bool preserveStatus = false)
    {
        try
        {
            var installed = _modelProvider.IsInstalled(_definition.ModelId);
            InstallStateTextBlock.Text = installed ? "取得済み" : "未取得 / 不完全";
            InstallButton.IsEnabled = !installed && !_isBusy;
            DeleteButton.IsEnabled = installed && !_isBusy;
            RefreshButton.IsEnabled = !_isBusy;

            var installationDirectory = Path.Combine(_modelProvider.ModelsRootDirectory, _definition.EngineId.Value, _definition.ModelId.Value);
            ModelPathTextBlock.Text = $"保存先: {installationDirectory}";

            if (!preserveStatus)
            {
                StatusTextBlock.Foreground = StatusDefaultBrush;
                StatusTextBlock.Text = installed
                    ? "必要な全ファイルのサイズとSHA-256が一致しています。"
                    : "モデルはまだ利用可能な状態ではありません。［モデル取得］から取得できます。";
            }
        }
        catch (Exception ex)
        {
            InstallStateTextBlock.Text = "確認失敗";
            InstallButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            StatusTextBlock.Foreground = StatusErrorBrush;
            StatusTextBlock.Text = $"モデル状態の確認に失敗しました: {ex.Message}";
        }
    }

    private void SetBusyState(bool isBusy, string? status)
    {
        _isBusy = isBusy;
        InstallButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        RefreshButton.IsEnabled = !isBusy;

        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusTextBlock.Foreground = StatusDefaultBrush;
            StatusTextBlock.Text = status;
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        return $"{bytes / mebibyte:F1} MiB";
    }
}
