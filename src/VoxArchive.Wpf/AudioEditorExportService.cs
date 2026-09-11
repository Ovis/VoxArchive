using System.Diagnostics;
using System.IO;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public enum AudioEditorExportFormat
{
    Wav,
    Mp3,
    Flac
}

/// <summary>
/// Audio Editorの編集状態をWAV/MP3/FLACへ安全に書き出す。
/// </summary>
public sealed class AudioEditorExportService
{
    private const double TargetPeakDbfs = -0.1d;
    private readonly AudioExportCoordinator _coordinator;

    public AudioEditorExportService(AudioExportCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task<AudioFileRenderService.RenderResult> ExportAsync(
        string inputFilePath,
        string outputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        AudioEditorExportFormat format,
        string? ffmpegExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(state);

        if (!state.HasOutputAudio)
        {
            throw new InvalidOperationException("編集後音声が空、または全チャンネルが無音のため書き出しできません。");
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("元音声ファイルへ上書きすることはできません。");
        }

        using var lease = await _coordinator.EnterAsync(cancellationToken);
        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-editor-export-{Guid.NewGuid():N}.wav");
        var extension = format switch
        {
            AudioEditorExportFormat.Wav => ".wav",
            AudioEditorExportFormat.Mp3 => ".mp3",
            _ => ".flac"
        };
        // ffmpegは出力拡張子からmuxerを選ぶため、tmp名でも最終形式の拡張子を残す。
        var tempOutputPath = Path.Combine(outputDirectory ?? Path.GetTempPath(), $".{Path.GetFileNameWithoutExtension(outputFullPath)}.{Guid.NewGuid():N}.tmp{extension}");
        try
        {
            var analysis = await AudioFileRenderService.AnalyzeAsync(inputFullPath, state, channelMode, cancellationToken);
            var masterGainDb = CalculateTargetMasterGainDb(analysis.PeakAbsoluteSample);
            var renderResult = await AudioFileRenderService.RenderWaveAsync(
                inputFullPath,
                tempWavePath,
                state,
                channelMode,
                masterGainDb,
                cancellationToken);

            await ConvertWithFfmpegAsync(tempWavePath, tempOutputPath, format, ffmpegExecutablePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(outputFullPath)) File.Delete(outputFullPath);
            File.Move(tempOutputPath, outputFullPath);
            return renderResult with
            {
                AppliedMasterGainDb = masterGainDb,
                AutoAttenuated = masterGainDb < -0.0000001d
            };
        }
        finally
        {
            TryDelete(tempWavePath);
            TryDelete(tempOutputPath);
        }
    }

    private static double CalculateTargetMasterGainDb(double peakAbsoluteSample)
    {
        if (peakAbsoluteSample <= 0d) return 0d;
        var targetLinear = Math.Pow(10d, TargetPeakDbfs / 20d);
        if (peakAbsoluteSample <= targetLinear) return 0d;
        return 20d * Math.Log10(targetLinear / peakAbsoluteSample);
    }

    private static async Task ConvertWithFfmpegAsync(
        string inputWavePath,
        string outputPath,
        AudioEditorExportFormat format,
        string? ffmpegExecutablePath,
        CancellationToken cancellationToken)
    {
        var codecArgs = format switch
        {
            AudioEditorExportFormat.Wav => "-c:a pcm_s16le",
            AudioEditorExportFormat.Mp3 => "-c:a libmp3lame -q:a 2",
            AudioEditorExportFormat.Flac => "-c:a flac -compression_level 5",
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
        if (!process.Start()) throw new InvalidOperationException("ffmpegを開始できませんでした。");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        var error = await process.StandardError.ReadToEndAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg変換に失敗しました (exit={process.ExitCode})。 {error}".Trim());
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
