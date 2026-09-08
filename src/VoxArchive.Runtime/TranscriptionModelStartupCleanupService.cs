using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Transcription.SileroVad;

namespace VoxArchive.Runtime;

/// <summary>
/// アプリ起動時に前回の異常終了などで残った文字起こしモデル管理用一時領域を掃除する
/// </summary>
/// <remarks>
/// SileroとReazonSpeechは同じVoxArchive管理の <c>models/.model-ops</c> を共有するため、
/// 起動時に1回だけcleanupすれば両者のdownload/backup/delete残骸を処理できる。
/// cleanup失敗はモデル利用可否やアプリ起動を妨げず、ログだけへ残す。
/// </remarks>
internal sealed class TranscriptionModelStartupCleanupService(
    SileroVadModelManager sileroModelManager,
    ILogger<TranscriptionModelStartupCleanupService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            sileroModelManager.CleanupTemporaryDirectories();
        }
        catch (Exception ex)
        {
            // cleanup自体の失敗で文字起こしやアプリ起動を停止しない。
            // 次回起動でも同じ所有領域を再試行するため、ここではログ記録だけに留める。
            logger.LogWarning(ex, "Failed to clean managed transcription model temporary directories at startup.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
