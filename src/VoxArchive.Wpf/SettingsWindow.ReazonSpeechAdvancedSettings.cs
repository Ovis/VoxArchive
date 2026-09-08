using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// SettingsWindowとReazonSpeech詳細設定Controlの編集バッファを接続する
/// </summary>
public partial class SettingsWindow
{
    private ReazonSpeechAdvancedSettingsControl? _reazonSpeechAdvancedSettingsControl;
    private IReadOnlyDictionary<string, string> _pendingReazonSpeechAdvancedSettings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["precision"] = "int8-fp32",
            ["decodingMethod"] = "greedy_search",
            ["maxActivePaths"] = "4",
            ["cpuThreads"] = Math.Min(4, Environment.ProcessorCount).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    /// <summary>ReazonSpeechの詳細設定編集値を取得・設定する</summary>
    public IReadOnlyDictionary<string, string> ReazonSpeechAdvancedSettings
    {
        get => _reazonSpeechAdvancedSettingsControl?.GetValues() ?? _pendingReazonSpeechAdvancedSettings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _pendingReazonSpeechAdvancedSettings = new Dictionary<string, string>(value, StringComparer.Ordinal);
            _reazonSpeechAdvancedSettingsControl?.ApplyValues(_pendingReazonSpeechAdvancedSettings);
        }
    }

    private void InitializeReazonSpeechAdvancedSettingsControl()
    {
        if (_reazonSpeechAdvancedSettingsControl is not null
            || TranscriptionTabControl.Items.Count <= 2
            || TranscriptionTabControl.Items[2] is not TabItem reazonTab
            || reazonTab.Content is not Grid reazonGrid)
        {
            return;
        }

        var rightColumn = reazonGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(x => Grid.GetColumn(x) == 2);
        if (rightColumn is null)
        {
            return;
        }

        // 既存のCPU固定・言語説明の下へ詳細設定を追加し、モデル管理と実行設定の責務を視覚的にも分ける。
        _reazonSpeechAdvancedSettingsControl = new ReazonSpeechAdvancedSettingsControl
        {
            Margin = new System.Windows.Thickness(0, 14, 0, 0)
        };
        _reazonSpeechAdvancedSettingsControl.ApplyValues(_pendingReazonSpeechAdvancedSettings);
        _reazonSpeechAdvancedSettingsControl.PrecisionChanged += OnReazonSpeechPrecisionChanged;
        rightColumn.Children.Add(_reazonSpeechAdvancedSettingsControl);
    }

    private void OnReazonSpeechPrecisionChanged(object? sender, EventArgs e)
    {
        // precision変更はモデル管理対象の物理packageを切り替えるため、保存前でも状態表示を即時更新する。
        if (_reazonSpeechTabVisited)
        {
            RefreshModelControl(ReazonSpeechEngineId, ReazonSpeechModelManagerControl);
        }
    }
}
