using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using ZLogger;

namespace VoxArchive.Infrastructure;

public sealed class ZLoggerRecordingTelemetrySink : IRecordingTelemetrySink, IDisposable
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private DateTimeOffset _lastStatisticsLogAt = DateTimeOffset.MinValue;

    public ZLoggerRecordingTelemetrySink(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        Directory.CreateDirectory(logsDirectory);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddZLoggerRollingFile(options =>
            {
                options.FilePathSelector = (timestamp, sequenceNumber) =>
                    Path.Combine(logsDirectory, $"recording-metrics-{timestamp.ToLocalTime():yyyyMMdd}-{sequenceNumber:000}.log");
                options.RollingSizeKB = 1024;
            });
        });

        _logger = _loggerFactory.CreateLogger("RecordingMetrics");
    }

    public void OnStateChanged(RecordingState state)
    {
        _logger.LogInformation("state={State}", state);
    }

    public void OnStatistics(RecordingStatistics statistics)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastStatisticsLogAt).TotalSeconds < 1)
        {
            return;
        }

        _lastStatisticsLogAt = now;
        _logger.LogInformation(
            "stats elapsed={Elapsed} spkBufMs={SpeakerBufferMs:F0} micBufMs={MicBufferMs:F0} ppm={DriftPpm:F1} uf={Underflow} of={Overflow} spkLv={SpeakerLevel:F3} micLv={MicLevel:F3}",
            statistics.ElapsedTime,
            statistics.SpeakerBufferMilliseconds,
            statistics.MicBufferMilliseconds,
            statistics.DriftCorrectionPpm,
            statistics.UnderflowCount,
            statistics.OverflowCount,
            statistics.SpeakerLevel,
            statistics.MicLevel);
    }

    public void OnError(string message)
    {
        _logger.LogWarning("error={Message}", message);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }
}
