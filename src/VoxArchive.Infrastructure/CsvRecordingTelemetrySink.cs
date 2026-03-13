using System.Globalization;
using System.Text;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class CsvRecordingTelemetrySink : IRecordingTelemetrySink
{
    private readonly string _csvPath;
    private readonly object _sync = new();
    private DateTimeOffset _lastStatisticsAt = DateTimeOffset.MinValue;
    private bool _headerWritten;

    public CsvRecordingTelemetrySink(string csvPath)
    {
        _csvPath = csvPath;
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _headerWritten = File.Exists(csvPath) && new FileInfo(csvPath).Length > 0;
    }

    public void OnStateChanged(RecordingState state)
    {
    }

    public void OnStatistics(RecordingStatistics statistics)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastStatisticsAt).TotalSeconds < 1)
        {
            return;
        }

        _lastStatisticsAt = now;

        lock (_sync)
        {
            if (!_headerWritten)
            {
                File.AppendAllText(_csvPath,
                    "timestamp,elapsed_seconds,speaker_level,mic_level,speaker_buffer_ms,mic_buffer_ms,drift_ppm,underflow_count,overflow_count,estimated_file_size_bytes,output_file_path" + Environment.NewLine,
                    Encoding.UTF8);
                _headerWritten = true;
            }

            var row = string.Join(',',
                Escape(now.ToString("O", CultureInfo.InvariantCulture)),
                statistics.ElapsedTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                statistics.SpeakerLevel.ToString("F6", CultureInfo.InvariantCulture),
                statistics.MicLevel.ToString("F6", CultureInfo.InvariantCulture),
                statistics.SpeakerBufferMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                statistics.MicBufferMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                statistics.DriftCorrectionPpm.ToString("F6", CultureInfo.InvariantCulture),
                statistics.UnderflowCount.ToString(CultureInfo.InvariantCulture),
                statistics.OverflowCount.ToString(CultureInfo.InvariantCulture),
                statistics.EstimatedFileSizeBytes.ToString(CultureInfo.InvariantCulture),
                Escape(statistics.OutputFilePath ?? string.Empty));

            File.AppendAllText(_csvPath, row + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void OnError(string message)
    {
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
