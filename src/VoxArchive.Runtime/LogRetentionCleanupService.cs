using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VoxArchive.Runtime;

/// <summary>
/// VoxArchiveの通常ログと文字起こし詳細診断へ同じ保持期間を適用する起動時cleanupを担当する
/// </summary>
internal sealed class LogRetentionCleanupService : IHostedService
{
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private readonly ILogger<LogRetentionCleanupService> _logger;
    private readonly string _logsDirectory;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 既定ログディレクトリとシステム時刻を使用してcleanup serviceを生成する
    /// </summary>
    public LogRetentionCleanupService(ILogger<LogRetentionCleanupService> logger)
        : this(logger, null, TimeProvider.System)
    {
    }

    internal LogRetentionCleanupService(
        ILogger<LogRetentionCleanupService> logger,
        string? logsDirectory,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxArchive",
                "logs")
            : logsDirectory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_logsDirectory)) return Task.CompletedTask;

        var cutoffUtc = _timeProvider.GetUtcNow() - RetentionPeriod;
        CleanupPattern("app-*.log", cutoffUtc, cancellationToken);
        CleanupPattern("*.transcription-diagnostic*.json", cutoffUtc, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void CleanupPattern(
        string searchPattern,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_logsDirectory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 通常ログと詳細診断を同じ30日境界で削除し、詳細診断だけが無期限に蓄積する状態を避ける。
                // LastWriteTimeUtcを使うことでファイル名のtimestamp形式やoffsetに保持判定を依存させない。
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // retention失敗はログ生成や文字起こし本体を止める理由にならないため、対象ファイル単位で警告して続行する。
                _logger.LogWarning(ex, "Failed to delete expired VoxArchive log file. File={File}", Path.GetFileName(path));
            }
        }
    }
}
