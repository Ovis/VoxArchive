using Microsoft.Extensions.Logging.Abstractions;
using VoxArchive.Runtime;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 通常ログと文字起こし詳細診断へ同じ保持期間が適用されることを確認する
/// </summary>
public sealed class LogRetentionCleanupServiceTests
{
    [Test]
    public async Task StartAsync_DeletesExpiredAppLogsAndDiagnosticsButKeepsRecentAndUnrelatedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero);
            var expiredAppLog = Path.Combine(root, "app-20260701-000.log");
            var recentAppLog = Path.Combine(root, "app-20260908-000.log");
            var expiredDiagnostic = Path.Combine(root, "meeting-20260701-120000.transcription-diagnostic.json");
            var recentDiagnostic = Path.Combine(root, "meeting-20260908-120000.transcription-diagnostic.json");
            var unrelated = Path.Combine(root, "keep.json");

            foreach (var path in new[] { expiredAppLog, recentAppLog, expiredDiagnostic, recentDiagnostic, unrelated })
            {
                await File.WriteAllTextAsync(path, "test");
            }

            File.SetLastWriteTimeUtc(expiredAppLog, now.UtcDateTime - LogRetentionCleanupService.RetentionPeriod - TimeSpan.FromDays(1));
            File.SetLastWriteTimeUtc(expiredDiagnostic, now.UtcDateTime - LogRetentionCleanupService.RetentionPeriod - TimeSpan.FromDays(1));
            File.SetLastWriteTimeUtc(recentAppLog, now.UtcDateTime - TimeSpan.FromDays(1));
            File.SetLastWriteTimeUtc(recentDiagnostic, now.UtcDateTime - TimeSpan.FromDays(1));
            File.SetLastWriteTimeUtc(unrelated, now.UtcDateTime - TimeSpan.FromDays(365));

            var service = new LogRetentionCleanupService(
                NullLogger<LogRetentionCleanupService>.Instance,
                root,
                new FixedTimeProvider(now));

            await service.StartAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(expiredAppLog), Is.False);
                Assert.That(File.Exists(expiredDiagnostic), Is.False);
                Assert.That(File.Exists(recentAppLog), Is.True);
                Assert.That(File.Exists(recentDiagnostic), Is.True);
                Assert.That(File.Exists(unrelated), Is.True);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
