using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Selection / CutRangeの限定確認再生をAudio Editorへ追加する。
/// </summary>
/// <remarks>
/// 確認再生の開始・終了位置は常に元音声時間で保持する。Edited Previewでは既存の
/// <see cref="AudioTimelineMapper"/>を通して実レンダリング時間へ変換するため、Cutと5ms Crossfadeを通常Previewと共有する。
/// </remarks>
public partial class AudioEditorWindow
{
    private AudioAuditionPlan? _auditionPlan;
    private bool _auditionUiInitialized;

    private void InitializeAuditionUi()
    {
        if (_auditionUiInitialized) return;
        _auditionUiInitialized = true;

        InitializeExportFeedback();
        _playbackTimer.Tick += OnAuditionTimerTick;
        CutRangeList.MouseDoubleClick += OnCutRangeListMouseDoubleClick;
        Closed += OnAuditionClosed;

        AddAuditionButtonsToCutPanel();
        AttachAuditionContextMenus();
    }

    private void AddAuditionButtonsToCutPanel()
    {
        var actions = CutEditPanel.Children
            .OfType<WrapPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 5);
        if (actions is null) return;

        var playSelection = new Button
        {
            Content = "選択範囲を再生",
            ToolTip = "通常Selection、または選択中CutRangeの削除対象原音を再生します"
        };
        playSelection.Click += OnAuditionSelectionClick;
        actions.Children.Add(playSelection);

        var boundary = new Button
        {
            Content = "境界を確認",
            ToolTip = "選択中CutRangeの前後約3秒を編集後音声として再生します",
            Margin = new Thickness(0)
        };
        boundary.Click += OnAuditionBoundaryClick;
        actions.Children.Add(boundary);
    }

    private void AttachAuditionContextMenus()
    {
        var cutMenu = new ContextMenu();
        var playDeleted = new MenuItem { Header = "削除部分の原音を再生" };
        playDeleted.Click += OnAuditionSelectionClick;
        cutMenu.Items.Add(playDeleted);
        var boundary = new MenuItem { Header = "境界を確認" };
        boundary.Click += OnAuditionBoundaryClick;
        cutMenu.Items.Add(boundary);
        CutRangeList.ContextMenu = cutMenu;

        var waveformMenu = new ContextMenu();
        var playSelection = new MenuItem { Header = "選択範囲を再生" };
        playSelection.Click += OnAuditionSelectionClick;
        waveformMenu.Items.Add(playSelection);
        WaveformControl.ContextMenu = waveformMenu;
    }

    private async void OnAuditionSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCutRange is { } selectedCut)
        {
            await StartAuditionAsync(AudioAuditionPlan.ForCutOriginal(selectedCut.Range));
            return;
        }

        if (!_viewModel.HasSelection || !_viewModel.SelectionStart.HasValue || !_viewModel.SelectionEnd.HasValue)
        {
            _viewModel.StatusText = "再生するSelectionまたはCutRangeを選択してください。";
            return;
        }

        var edited = PreviewModeComboBox.SelectedIndex == 0;
        await StartAuditionAsync(AudioAuditionPlan.ForSelection(
            _viewModel.SelectionStart.Value,
            _viewModel.SelectionEnd.Value,
            edited));
    }

    private async void OnAuditionBoundaryClick(object sender, RoutedEventArgs e)
    {
        await StartSelectedCutBoundaryAuditionAsync();
    }

    private async void OnCutRangeListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedCutRange is null) return;
        e.Handled = true;
        await StartSelectedCutBoundaryAuditionAsync();
    }

    private Task StartSelectedCutBoundaryAuditionAsync()
    {
        if (_viewModel.SelectedCutRange is not { } selectedCut)
        {
            _viewModel.StatusText = "境界を確認するCutRangeを選択してください。";
            return Task.CompletedTask;
        }

        return StartAuditionAsync(AudioAuditionPlan.ForCutBoundary(
            selectedCut.Range,
            _viewModel.SourceDuration));
    }

    private async Task StartAuditionAsync(AudioAuditionPlan plan)
    {
        if (_viewModel.CurrentState is not { } state) return;

        try
        {
            _sourceGuard.ValidateUnchanged();
            _previewService.Pause();
            _auditionPlan = plan;

            var speed = GetSelectedPlaybackSpeed();
            if (plan.Kind == AudioAuditionKind.CutOriginal || !plan.UsesEditedTimeline)
            {
                // Cut原音確認ではCut/Gain/Mute/出力モードをすべて無視する。SoloだけはPreviewService側で反映する。
                await _previewService.PlayOriginalAsync(
                    _viewModel.SourceFilePath,
                    state,
                    _soloChannel,
                    speed,
                    _lifetimeCancellation.Token);
                _previewService.Seek(plan.SourceStart);
            }
            else
            {
                await _previewService.PlayEditedAsync(
                    _viewModel.SourceFilePath,
                    state,
                    GetSelectedChannelMode(),
                    _soloChannel,
                    speed,
                    _lifetimeCancellation.Token);
                var mapper = GetTimelineMapper();
                var start = mapper.ResolveSourceSeekTarget(plan.SourceStart, AudioSeekDirection.Forward);
                _previewService.Seek(mapper.SourceToRendered(start, AudioSeekDirection.Forward));
            }

            SetPlayhead(plan.SourceStart);
            UpdateAuditionModeUi();
            _viewModel.StatusText = plan.Kind switch
            {
                AudioAuditionKind.CutOriginal => "削除対象区間の原音を確認しています。",
                AudioAuditionKind.CutBoundary => "Cut境界の編集後接続を確認しています。",
                _ => plan.UsesEditedTimeline ? "選択範囲を編集後モードで再生しています。" : "選択範囲を原音モードで再生しています。"
            };
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            ClearAuditionMode();
        }
        catch (Exception ex)
        {
            ClearAuditionMode();
            _viewModel.StatusText = $"確認再生に失敗しました: {ex.Message}";
        }
    }

    private void OnAuditionTimerTick(object? sender, EventArgs e)
    {
        if (_auditionPlan is not { } plan) return;

        // 通常のStopはPlaybackServiceのPositionを0へ戻す。Pauseでは位置を保持するため、ここで確認モードを区別できる。
        if (!_previewService.IsPlaying && _previewService.IsLoaded && _previewService.Position <= TimeSpan.FromMilliseconds(1))
        {
            ClearAuditionMode();
            return;
        }

        if (!_previewService.IsPlaying) return;

        var currentSource = _previewService.IsEditedMode
            ? GetTimelineMapper().RenderedToSource(_previewService.Position)
            : _previewService.Position;
        if (currentSource < plan.SourceEnd) return;

        _previewService.Pause();
        SetPlayhead(plan.SourceEnd);
        ClearAuditionMode();
        _viewModel.StatusText = "確認範囲の終端で停止しました。";
    }

    private void UpdateAuditionModeUi()
    {
        // CutRange原音確認だけはグローバルA/B設定を変更せず、その切替操作も確認中は無効にする。
        PreviewModeComboBox.IsEnabled = _auditionPlan?.Kind != AudioAuditionKind.CutOriginal;
    }

    private void ClearAuditionMode()
    {
        _auditionPlan = null;
        if (IsLoaded) PreviewModeComboBox.IsEnabled = true;
    }

    private void CancelAuditionForTimelineEdit()
    {
        if (_auditionPlan is null) return;
        _previewService.Stop();
        ClearAuditionMode();
    }

    private void OnAuditionClosed(object? sender, EventArgs e)
    {
        _playbackTimer.Tick -= OnAuditionTimerTick;
        CutRangeList.MouseDoubleClick -= OnCutRangeListMouseDoubleClick;
    }
}
