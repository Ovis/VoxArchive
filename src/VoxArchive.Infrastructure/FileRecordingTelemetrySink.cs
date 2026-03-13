using System.Text;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class FileRecordingTelemetrySink : IRecordingTelemetrySink, IDisposable
{
    private readonly string _logFilePath;
    private readonly object _sync = new();
    private DateTimeOffset _lastStatisticsLogAt = DateTimeOffset.MinValue;

    public FileRecordingTelemetrySink(string logFilePath)
    {
        _logFilePath = logFilePath;
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void OnStateChanged(RecordingState state)
    {
        WriteLine($"state={state}");
    }

    public void OnStatistics(RecordingStatistics statistics)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastStatisticsLogAt).TotalSeconds < 1)
        {
            return;
        }

        _lastStatisticsLogAt = now;
        WriteLine($"stats elapsed={statistics.ElapsedTime:hh\\:mm\\:ss} spkBufMs={statistics.SpeakerBufferMilliseconds:F0} micBufMs={statistics.MicBufferMilliseconds:F0} ppm={statistics.DriftCorrectionPpm:F1} uf={statistics.UnderflowCount} of={statistics.OverflowCount} spkLv={statistics.SpeakerLevel:F3} micLv={statistics.MicLevel:F3}");
    }

    public void OnError(string message)
    {
        WriteLine($"error={message}");
    }

    public void Dispose()
    {
    }

    private void WriteLine(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (_sync)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
