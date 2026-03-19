using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
            await ConvertWithFfmpegAsync(tempWavePath, outputFilePath, format, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempWavePath))
                {
                    File.Delete(tempWavePath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗は出力結果に影響しないため握りつぶす。
            }
        }
    }

    public static Task ExportAsMonoWaveAsync(
        string inputFilePath,
        string outputFilePath,
        double speakerGainDb,
        double micGainDb,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var reader = new AudioFileReader(inputFilePath);
            var channels = Math.Max(1, reader.WaveFormat.Channels);

            ISampleProvider provider;
            if (channels >= 2)
            {
                var gainProvider = new StereoGainSampleProvider(reader)
                {
                    LeftGain = DbToLinearGain(speakerGainDb),
                    RightGain = DbToLinearGain(micGainDb),
                    MixToMono = true
                };

                provider = new StereoToMonoSampleProvider(gainProvider)
                {
                    LeftVolume = 1f,
                    RightVolume = 0f
                };
            }
            else
            {
                provider = new VolumeSampleProvider(reader)
                {
                    Volume = DbToLinearGain(speakerGainDb)
                };
            }

            WaveFileWriter.CreateWaveFile16(outputFilePath, provider);
        }, cancellationToken);
    }

    private static async Task ConvertWithFfmpegAsync(string inputWavePath, string outputPath, MonoMixdownOutputFormat format, CancellationToken cancellationToken)
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
            FileName = "ffmpeg",
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

        await process.WaitForExitAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg conversion failed (exit={process.ExitCode}). {error}".Trim());
        }
    }

    private static float DbToLinearGain(double db)
    {
        var linear = Math.Pow(10d, db / 20d);
        if (linear < 0.001d)
        {
            return 0f;
        }

        return (float)linear;
    }
}
