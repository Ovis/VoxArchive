using System.Windows;
using System.Windows.Controls;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 設定画面へ文字起こしEngineの選択UIとEngine別表示切替を追加する
/// </summary>
/// <remarks>
/// 既存のSettingsWindowはWhisper前提で構成されているため、Engine選択機能を段階的に導入する。
/// このクラスでは永続化に使う安定IDと既存Whisper設定UIの活性状態だけを管理し、
/// Engine固有の認識処理やモデル物理構成は各Provider/Engineへ委譲する。
/// </remarks>
public partial class SettingsWindow
{
    private ComboBox? _transcriptionEngineComboBox;
    private string _defaultTranscriptionEngine = TranscriptionEngineId.Whisper.Value;
    private string _reazonSpeechModelId = "ja";

    /// <summary>
    /// 新規文字起こしで既定として使用するEngineの安定IDを取得・設定する
    /// </summary>
    public string DefaultTranscriptionEngine
    {
        get => _defaultTranscriptionEngine;
        set
        {
            _defaultTranscriptionEngine = NormalizeEngineId(value);
            SelectEngineComboItem(_defaultTranscriptionEngine);
            UpdateEngineSpecificUi();
        }
    }

    /// <summary>
    /// ReazonSpeechで使用する論理モデルIDを取得・設定する
    /// </summary>
    /// <remarks>
    /// 現在の公式対応モデルは日本語k2-v2の1種類だけだが、設定形式では将来のモデル追加に備えてIDを保持する。
    /// </remarks>
    public string ReazonSpeechModelId
    {
        get => _reazonSpeechModelId;
        set => _reazonSpeechModelId = string.IsNullOrWhiteSpace(value) ? "ja" : value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 既存の文字起こし設定領域へEngine選択欄を追加する
    /// </summary>
    /// <remarks>
    /// 現在のXAMLはWhisper前提のため、この段階では既存レイアウトを壊さずに選択欄だけを挿入する。
    /// 設定画面全体のレイアウト再編時にXAMLへ統合することを前提とした移行コードである。
    /// </remarks>
    private void EnsureTranscriptionEngineSelector()
    {
        if (_transcriptionEngineComboBox is not null || ModelComboBox.Parent is not StackPanel settingsPanel)
        {
            return;
        }

        var label = new TextBlock
        {
            Text = "既定Engine",
            Margin = new Thickness(0, 8, 0, 6)
        };
        if (TryFindResource("FieldLabelStyle") is Style labelStyle)
        {
            label.Style = labelStyle;
            label.Margin = new Thickness(0, 8, 0, 6);
        }

        _transcriptionEngineComboBox = new ComboBox
        {
            Width = double.NaN,
            SelectedValuePath = "Tag",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _transcriptionEngineComboBox.Items.Add(new ComboBoxItem
        {
            Content = "Whisper",
            Tag = TranscriptionEngineId.Whisper.Value
        });
        _transcriptionEngineComboBox.Items.Add(new ComboBoxItem
        {
            Content = "ReazonSpeech",
            Tag = TranscriptionEngineId.ReazonSpeech.Value
        });
        _transcriptionEngineComboBox.SelectionChanged += OnTranscriptionEngineSelectionChanged;

        // 左側先頭の3つのCheckBoxは共通設定なので、その直後にEngine選択を置く。
        // Whisper固有の実行モード/モデルより前に置くことで、以下の項目が選択Engineに従属することを明示する。
        var insertionIndex = Math.Min(3, settingsPanel.Children.Count);
        settingsPanel.Children.Insert(insertionIndex, label);
        settingsPanel.Children.Insert(insertionIndex + 1, _transcriptionEngineComboBox);

        SelectEngineComboItem(_defaultTranscriptionEngine);
        RefreshModelDownloadUi();
        UpdateEngineSpecificUi();
    }

    private void OnTranscriptionEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_transcriptionEngineComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string engineId)
        {
            _defaultTranscriptionEngine = NormalizeEngineId(engineId);
        }

        // Engine切替前のWhisperモデル取得状態がボタンへ残らないよう、
        // Engine固有表示を更新する前にモデル管理UIの可用性も再評価する。
        SetDefaultEnvironmentStatus();
        RefreshModelDownloadUi();
        UpdateEngineSpecificUi();
    }

    private void SelectEngineComboItem(string engineId)
    {
        if (_transcriptionEngineComboBox is null)
        {
            return;
        }

        foreach (var item in _transcriptionEngineComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), engineId, StringComparison.OrdinalIgnoreCase))
            {
                _transcriptionEngineComboBox.SelectedItem = item;
                return;
            }
        }

        _transcriptionEngineComboBox.SelectedIndex = 0;
    }

    private void UpdateEngineSpecificUi()
    {
        if (!IsInitialized)
        {
            return;
        }

        var isWhisper = string.Equals(_defaultTranscriptionEngine, TranscriptionEngineId.Whisper.Value, StringComparison.OrdinalIgnoreCase);

        // ReazonSpeechは現段階ではCPU固定かつ日本語モデル1種類なので、Whisper固有の設定は編集不可にする。
        // 値自体は保持しておき、Whisperへ戻したときに以前の設定をそのまま再利用できるようにする。
        ExecutionModeComboBox.IsEnabled = isWhisper;
        ModelComboBox.IsEnabled = isWhisper;
        LanguageComboBox.IsEnabled = isWhisper;
        CheckEnvironmentButton.IsEnabled = isWhisper;

        if (!isWhisper)
        {
            TranscriptionStatusTextBlock.Foreground = StatusDefaultBrush;
            TranscriptionStatusTextBlock.Text = "ReazonSpeech: 日本語（k2-v2）/ CPU固定。モデル管理から取得状態を確認できます。";
        }
    }

    private static string NormalizeEngineId(string? engineId)
    {
        return string.Equals(engineId?.Trim(), TranscriptionEngineId.ReazonSpeech.Value, StringComparison.OrdinalIgnoreCase)
            ? TranscriptionEngineId.ReazonSpeech.Value
            : TranscriptionEngineId.Whisper.Value;
    }
}
