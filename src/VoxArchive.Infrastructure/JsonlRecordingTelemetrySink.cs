using System.Text;
using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class JsonlRecordingTelemetrySink : IRecordingTelemetrySink
{
    private readonly string _jsonlPath;
    private readonly object _sync = new();
    private DateTimeOffset _lastStatisticsAt = DateTimeOffset.MinValue;

    public JsonlRecordingTelemetrySink(string jsonlPath)
    {
        _jsonlPath = jsonlPath;
        var dir = Path.GetDirectoryName(jsonlPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void OnStateChanged(RecordingState state)
    {
        Write(new
        {
            type = "state",
            timestamp = DateTimeOffset.UtcNow,
            state = state.ToString()
        });
    }

    public void OnStatistics(RecordingStatistics statistics)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastStatisticsAt).TotalSeconds < 1)
        {
            return;
        }

        _lastStatisticsAt = now;

        Write(new
        {
            type = "stats",
            timestamp = now,
            elapsed_seconds = statistics.ElapsedTime.TotalSeconds,
            speaker_level = statistics.SpeakerLevel,
            mic_level = statistics.MicLevel,
            speaker_buffer_ms = statistics.SpeakerBufferMilliseconds,
            mic_buffer_ms = statistics.MicBufferMilliseconds,
            drift_ppm = statistics.DriftCorrectionPpm,
            underflow_count = statistics.UnderflowCount,
            overflow_count = statistics.OverflowCount,
            estimated_file_size_bytes = statistics.EstimatedFileSizeBytes,
            output_file_path = statistics.OutputFilePath
        });
    }

    public void OnError(string message)
    {
        Write(new
        {
            type = "error",
            timestamp = DateTimeOffset.UtcNow,
            message
        });
    }

    private void Write(object payload)
    {
        var line = JsonSerializer.Serialize(payload);
        lock (_sync)
        {
            File.AppendAllText(_jsonlPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
