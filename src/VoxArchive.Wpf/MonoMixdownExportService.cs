using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public enum MonoMixdownOutputFormat
{
    Wav,
    Mp3,
    Flac
}

public static class MonoMixdownExportService
{
    public static async Task ExportAsync(
        string inputFilePath,
        string outputFilePath,
        double speakerGainDb,
        double micGainDb,
        MonoMixdownOutputFormat format,
        string? ffmpegExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        if (format == MonoMixdownOutputFormat.Wav)
        {
            await ExportAsMonoWaveAsync(inputFilePath, outputFilePath, speakerGainDb, micGainDb, cancellationToken);
            return;
        }

        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-mono-{Guid.NewGuid():N}.wav");
        try
        {
            await ExportAsMonoWaveAsync(inputFilePath, tempWavePath, speakerGainDb, micGainDb, cancellationToken);
            await ConvertWithFfmpegAsync(tempWavePath, outputFilePath, format, ffmpegExecutablePath, cancellationToken);
        }
        finally
        {
            TryDelete(tempWavePath);
        }
    }

    /// <summary>
    /// 既存Libraryのモノラル書き出しもAudio Editorと同じDSP・Clipping防止経路で処理する。
    /// </summary>
    public static async Task ExportAsMonoWaveAsync(
        string inputFilePath,
        string outputFilePath,
        double speakerGainDb,
        double micGainDb,
        CancellationToken cancellationToken = default)
    {
        AudioEditState state;
        using (var reader = new AudioFileReader(inputFilePath))
        {
            var channels = reader.WaveFormat.Channels;
            if (channels is < 1 or > 2)
            {
                throw new NotSupportedException("モノラル変換はMonoまたはStereo音声のみ対応しています。");
            }

            var channelStates = channels == 1
                ? new[] { new AudioChannelEditState(speakerGainDb) }
                : new[]
                {
                    new AudioChannelEditState(speakerGainDb),
                    new AudioChannelEditState(micGainDb)
                };

            state = new AudioEditState(reader.TotalTime, channels, channelStates: channelStates);
        }

        await AudioFileRenderService.RenderWaveAsync(
            inputFilePath,
            outputFilePath,
            state,
            AudioRenderChannelMode.MonoMixdown,
            cancellationToken: cancellationToken);
    }

    private static async Task ConvertWithFfmpegAsync(
        string inputWavePath,
        string outputPath,
        MonoMixdownOutputFormat format,
        string? ffmpegExecutablePath,
        CancellationToken cancellationToken)
    {
        var codecArgs = format switch
        {
            MonoMixdownOutputFormat.Mp3 => "-c:a libmp3lame -q:a 2",
            MonoMixdownOutputFormat.Flac => "-c:a flac -compression_level 5",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

        var args = $"-y -hide_banner -loglevel error -i \"{inputWavePath}\" {codecArgs} \"{outputPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegExecutablePath) ? "ffmpeg" : ffmpegExecutablePath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(outputPath);
            throw;
        }

        var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            throw new InvalidOperationException($"ffmpeg conversion failed (exit={process.ExitCode}). {error}".Trim());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
