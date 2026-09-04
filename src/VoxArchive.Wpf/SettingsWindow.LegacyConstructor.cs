using Microsoft.Extensions.DependencyInjection;

namespace VoxArchive.Wpf;

public partial class SettingsWindow
{
    /// <summary>
    /// 既存の設定画面呼び出し経路から、アプリケーション共有のモデル管理サービスを補完して初期化する
    /// </summary>
    /// <remarks>
    /// モデル取得はWindowより長く生存する必要があるため、新しいManagerをここで生成せずDIコンテナのSingletonを利用する。
    /// 呼び出し側を段階的にDI解決へ寄せる間の互換入口として残す。
    /// </remarks>
    public SettingsWindow(
        WhisperModelStore whisperModelStore,
        WhisperTranscriptionService whisperTranscriptionService)
    {
        var app = System.Windows.Application.Current as App
            ?? throw new InvalidOperationException("VoxArchiveアプリケーションを取得できません。");

        _whisperModelStore = whisperModelStore;
        _whisperTranscriptionService = whisperTranscriptionService;
        _modelManager = app.Services.GetRequiredService<TranscriptionModelManager>();
        InitializeWindow();
    }
}
