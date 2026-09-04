using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Window内の文字起こしタブについて、WPFのTabPanel描画に起因する罫線欠けを補正する
/// </summary>
public partial class SettingsWindow
{
    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureTranscriptionTabBordersVisible();
    }

    /// <summary>
    /// 各文字起こしタブの右罫線がTabPanelの配置境界で欠けないよう、テンプレート内に描画余白を確保する
    /// </summary>
    private void EnsureTranscriptionTabBordersVisible()
    {
        foreach (var tabItem in TranscriptionTabControl.Items.OfType<TabItem>())
        {
            tabItem.ApplyTemplate();
            if (tabItem.Template.FindName("TabRoot", tabItem) is not Border tabRoot)
            {
                continue;
            }

            // TabPanelはヘッダーを隣接配置する際、テンプレート最右端の1pxを描画境界でクリップすることがある。
            // タブ間の既存Marginは変えず、Border自身の右側に1pxだけ余白を設けて右罫線を内側へ収める。
            tabRoot.Margin = new Thickness(0, 0, 1, 0);
        }
    }
}
