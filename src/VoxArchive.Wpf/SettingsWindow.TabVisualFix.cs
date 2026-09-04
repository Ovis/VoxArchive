using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Windowの文字起こしタブへ専用ResourceDictionaryのスタイルを適用する
/// </summary>
public partial class SettingsWindow
{
    private bool _transcriptionTabStylesApplied;

    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyTranscriptionTabStyles();
    }

    /// <summary>
    /// 文字起こしタブの見た目を、TabPanelに依存しない専用テンプレートへ差し替える
    /// </summary>
    private void ApplyTranscriptionTabStyles()
    {
        if (_transcriptionTabStylesApplied)
        {
            return;
        }

        _transcriptionTabStylesApplied = true;

        // SettingsWindow.xamlに残る旧スタイルは既存画面の読み込み互換のためそのままにし、
        // 描画時には専用ResourceDictionaryのV2スタイルへ明示的に差し替える。
        // これにより、TabPanel特有の端クリップとTabItem全体へ伝播するHover判定を切り離す。
        var resources = new ResourceDictionary
        {
            Source = new Uri("/VoxArchive.Wpf;component/TranscriptionTabStyles.xaml", UriKind.Relative)
        };

        if (resources["TranscriptionTabControlStyleV2"] is not Style tabControlStyle
            || resources["TranscriptionTabItemStyleV2"] is not Style tabItemStyle)
        {
            throw new InvalidOperationException("文字起こしタブ用スタイルを読み込めませんでした。");
        }

        TranscriptionTabControl.Style = tabControlStyle;
        foreach (var tabItem in TranscriptionTabControl.Items.OfType<TabItem>())
        {
            tabItem.Style = tabItemStyle;
        }
    }
}
