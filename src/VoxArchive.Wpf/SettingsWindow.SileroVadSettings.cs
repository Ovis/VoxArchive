using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// SettingsWindowとSilero VAD設定Controlの編集値を接続する
/// </summary>
public partial class SettingsWindow
{
    private SileroVadSettings _sileroVadSettings = new();

    /// <summary>
    /// 設定画面で編集中のSilero VAD設定を取得・設定する
    /// </summary>
    /// <remarks>
    /// Controlが存在しないテスト用Windowでも値を保持できるようbacking fieldを持つ。
    /// 実アプリではControlの編集バッファを正本とし、親Windowの保存までは永続化しない。
    /// </remarks>
    public SileroVadSettings SileroVadSettings
    {
        get => _speechRegionDetectorSettingsControl?.Settings ?? _sileroVadSettings;
        set
        {
            _sileroVadSettings = value ?? throw new ArgumentNullException(nameof(value));
            if (_speechRegionDetectorSettingsControl is not null)
            {
                _speechRegionDetectorSettingsControl.Settings = value;
            }
        }
    }
}
