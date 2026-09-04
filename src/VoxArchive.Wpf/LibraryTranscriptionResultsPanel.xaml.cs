using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで文字起こし結果の一覧・メタデータ・本文と結果操作を表示するパネル
/// </summary>
/// <remarks>
/// 結果の発見・本文ロード・派生出力・正本削除は <see cref="LibraryTranscriptionResultsState"/> に委譲し、
/// 本クラスは選択操作と確認ダイアログなどUI責務だけを持つ。
/// </remarks>
public partial class LibraryTranscriptionResultsPanel : UserControl
{
    private LibraryTranscriptionResultsState? _state;
    private bool _selectionChangeInProgress;
    private Popup? _exportPopup;

    /// <summary>エディタで開く操作が要求されたときに発生する</summary>
    public event EventHandler? OpenInEditorRequested;

    /// <summary>独立ウィンドウで開く操作が要求されたときに発生する</summary>
    public event EventHandler? OpenDetachedRequested;

    /// <summary>選択結果の再文字起こしが要求されたときに発生する</summary>
    public event EventHandler? RetranscribeRequested;

    /// <summary>パネル上部の見出しを表示するかどうかを取得または設定する</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>パネル下部の操作ボタンを表示するかどうかを取得または設定する</summary>
    public bool ShowActions { get; set; } = true;

    /// <summary>本文表示領域の高さを取得または設定する</summary>
    public GridLength TranscriptHeight { get; set; } = new(180);

    /// <summary>表示対象の文字起こし状態を取得または設定する</summary>
    public LibraryTranscriptionResultsState? State
    {
        get => _state;
        set
        {
            _state = value;
            DataContext = value;
        }
    }

    /// <summary>XAMLから生成するためのパネルを初期化する</summary>
    public LibraryTranscriptionResultsPanel()
    {
        InitializeComponent();
    }

    /// <summary>指定された状態を表示するパネルを生成する</summary>
    public LibraryTranscriptionResultsPanel(LibraryTranscriptionResultsState state) : this()
    {
        State = state;
    }

    private async void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionChangeInProgress || _state is null ||
            sender is not ComboBox { SelectedItem: LibraryTranscriptionResultItem selected })
        {
            return;
        }

        try
        {
            _selectionChangeInProgress = true;
            await _state.SelectAsync(selected);
        }
        finally
        {
            _selectionChangeInProgress = false;
        }
    }

    private void OnOpenInEditorClick(object sender, RoutedEventArgs e)
        => OpenInEditorRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenDetachedClick(object sender, RoutedEventArgs e)
        => OpenDetachedRequested?.Invoke(this, EventArgs.Empty);

    private void OnRetranscribeClick(object sender, RoutedEventArgs e)
        => RetranscribeRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        // ContextMenuはWindowsテーマ側の描画領域が残るため、外観を制御できるPopupを使用する。
        _exportPopup ??= CreateExportPopup();
        _exportPopup.PlacementTarget = button;
        _exportPopup.IsOpen = true;
    }

    private Popup CreateExportPopup()
    {
        var panel = new StackPanel { Width = 160 };
        panel.Children.Add(CreateExportPopupButton("TXT", TranscriptionOutputFormats.Txt));
        panel.Children.Add(CreateExportPopupButton("SRT", TranscriptionOutputFormats.Srt));
        panel.Children.Add(CreateExportPopupButton("VTT", TranscriptionOutputFormats.Vtt));
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x60))
        });
        panel.Children.Add(CreateExportPopupButton(
            "すべて出力",
            TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt | TranscriptionOutputFormats.Vtt));

        return new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x3C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x46, 0x64)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                Child = panel
            }
        };
    }

    private Button CreateExportPopupButton(string label, TranscriptionOutputFormats formats)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("TranscriptionExportPopupButtonStyle")
        };

        button.Click += async (_, _) =>
        {
            if (_exportPopup is not null)
            {
                _exportPopup.IsOpen = false;
            }
            await ExportAsync(formats);
        };
        return button;
    }

    private async Task ExportAsync(TranscriptionOutputFormats formats)
    {
        if (_state?.SelectedResult is null)
        {
            return;
        }

        try
        {
            var generated = await _state.ExportSelectedAsync(formats);
            ModernDialog.Show(
                $"{generated.Count} 件の派生ファイルを出力しました。\n{string.Join(Environment.NewLine, generated.Select(Path.GetFileName))}",
                "文字起こし出力",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ModernDialog.Show($"文字起こし結果の出力に失敗しました。\n{ex.Message}", "文字起こし出力エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_state?.SelectedResult is not { } selected)
        {
            return;
        }

        var result = ModernDialog.Show(
            $"選択中の文字起こし結果を削除します。\n{selected.DisplayName}\n\nTXT/SRT/VTTなどの派生ファイルは削除せず残します。",
            "文字起こし結果の削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _state.DeleteSelectedAsync();
        }
        catch (Exception ex)
        {
            ModernDialog.Show($"文字起こし結果を削除できませんでした。\n{ex.Message}", "文字起こし削除エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
