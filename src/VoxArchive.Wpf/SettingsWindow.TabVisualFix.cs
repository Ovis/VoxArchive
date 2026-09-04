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
    /// 各文字起こしタブの右罫線がTabPanelの配置境界で欠けないよう、右罫線を1px内側にも描画する
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

            // TabPanel側でテンプレート最右端の1pxが欠けても内側の1pxが残るよう、
            // 右辺だけ2pxで描画する。外形やタブ間隔を動かさないためMarginでは補正しない。
            tabRoot.BorderThickness = new Thickness(1, 1, 2, 1);
            tabRoot.SnapsToDevicePixels = true;
            tabRoot.UseLayoutRounding = true;
        }
    }
}
