using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定Window内の文字起こしタブについて、ヘッダー部分だけにHover表現を適用し、右罫線を確実に描画する
/// </summary>
public partial class SettingsWindow
{
    private static readonly Brush TranscriptionTabNormalBackground = CreateFrozenBrush("#10161F");
    private static readonly Brush TranscriptionTabHoverBackground = CreateFrozenBrush("#172437");
    private static readonly Brush TranscriptionTabSelectedBackground = CreateFrozenBrush("#1A2B40");
    private static readonly Brush TranscriptionTabNormalBorder = CreateFrozenBrush("#283242");
    private static readonly Brush TranscriptionTabSelectedBorder = CreateFrozenBrush("#3D5D84");

    private bool _transcriptionTabVisualsInitialized;

    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InitializeTranscriptionTabVisuals();
    }

    /// <summary>
    /// TabItem全体のIsMouseOverに依存せず、実際のヘッダーBorderだけを対象にHoverと右罫線を構成する
    /// </summary>
    private void InitializeTranscriptionTabVisuals()
    {
        if (_transcriptionTabVisualsInitialized)
        {
            RefreshTranscriptionTabVisuals();
            return;
        }

        _transcriptionTabVisualsInitialized = true;
        TranscriptionTabControl.SelectionChanged += OnTranscriptionTabVisualSelectionChanged;

        foreach (var tabItem in TranscriptionTabControl.Items.OfType<TabItem>())
        {
            tabItem.ApplyTemplate();
            if (tabItem.Template.FindName("TabRoot", tabItem) is not Border tabRoot)
            {
                continue;
            }

            // TabItem.IsMouseOverは選択中コンテンツ配下までtrueになるため、XAML側のTriggerだけでは
            // 本文上へマウスを置いた際にもヘッダー色が変化する。Borderへローカル値を設定し、
            // ヘッダー自身のMouseEnter/LeaveだけでHover状態を制御する。
            var rightEdge = EnsureRightEdge(tabRoot);
            ApplyTabVisual(tabItem, tabRoot, rightEdge, isHeaderHovered: false);

            tabRoot.MouseEnter += (_, _) => ApplyTabVisual(tabItem, tabRoot, rightEdge, isHeaderHovered: true);
            tabRoot.MouseLeave += (_, _) => ApplyTabVisual(tabItem, tabRoot, rightEdge, isHeaderHovered: false);
        }
    }

    /// <summary>
    /// タブ選択が変わったときに、各ヘッダーの背景色と罫線色を現在の選択状態へ同期する
    /// </summary>
    private void OnTranscriptionTabVisualSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTranscriptionTabVisuals();
    }

    /// <summary>
    /// 現在の選択状態とヘッダー上のHover状態から、全タブの見た目を再計算する
    /// </summary>
    private void RefreshTranscriptionTabVisuals()
    {
        foreach (var tabItem in TranscriptionTabControl.Items.OfType<TabItem>())
        {
            tabItem.ApplyTemplate();
            if (tabItem.Template.FindName("TabRoot", tabItem) is not Border tabRoot)
            {
                continue;
            }

            var rightEdge = EnsureRightEdge(tabRoot);
            ApplyTabVisual(tabItem, tabRoot, rightEdge, tabRoot.IsMouseOver);
        }
    }

    /// <summary>
    /// TabPanel境界で外周Borderの右辺が欠けても見えるよう、ヘッダー内部へ独立した1px罫線を追加する
    /// </summary>
    private static Border EnsureRightEdge(Border tabRoot)
    {
        if (tabRoot.Child is Grid existingGrid
            && existingGrid.Children.OfType<Border>().FirstOrDefault(child => Equals(child.Tag, "TranscriptionTabRightEdge")) is { } existingEdge)
        {
            return existingEdge;
        }

        var originalChild = tabRoot.Child;
        var container = new Grid();
        tabRoot.Child = container;
        if (originalChild is not null)
        {
            container.Children.Add(originalChild);
        }

        var rightEdge = new Border
        {
            Tag = "TranscriptionTabRightEdge",
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        container.Children.Add(rightEdge);
        return rightEdge;
    }

    /// <summary>
    /// 選択状態とヘッダーHover状態から、背景色と右罫線色を同期する
    /// </summary>
    private static void ApplyTabVisual(TabItem tabItem, Border tabRoot, Border rightEdge, bool isHeaderHovered)
    {
        tabRoot.Background = isHeaderHovered
            ? TranscriptionTabHoverBackground
            : tabItem.IsSelected
                ? TranscriptionTabSelectedBackground
                : TranscriptionTabNormalBackground;

        rightEdge.Background = tabItem.IsSelected
            ? TranscriptionTabSelectedBorder
            : TranscriptionTabNormalBorder;
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
