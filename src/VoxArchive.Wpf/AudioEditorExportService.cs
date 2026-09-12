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

public enum AudioEditorExportStage
{
    Preparing,
    PeakAnalysis,
    Rendering,
    Encoding,
    Committing,
    LibraryRegistration,
    Completed
}

public sealed record AudioEditorExportProgress(
    AudioEditorExportStage Stage,
    double Progress,
    string Message);

public sealed record AudioEditorExportResult(
    AudioFileRenderService.RenderResult RenderResult,
    bool LibraryRegistrationAttempted,
    bool LibraryRegistrationSucceeded,
    string? LibraryRegistrationError);

/// <summary>
/// Audio Editorの編集状態をWAV/MP3/FLACへ安全に書き出す。
/// </summary>
public sealed class AudioEditorExportService
{
    private readonly AudioExportCoordinator _coordinator;
    private readonly RecordingCatalogService _catalogService;

    public AudioEditorExportService(AudioExportCoordinator coordinator, RecordingCatalogService catalogService)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    }

    /// <summary>
    /// Exportの現在段階をUIへ通知する。
    /// </summary>
    public event EventHandler<AudioEditorExportProgress>? ProgressChanged;

    public async Task<AudioEditorExportResult> ExportAsync(
        string inputFilePath,
        string outputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        AudioEditorExportFormat format,
        bool autoAttenuate,
        bool addFlacToLibrary,
        string libraryTitle,
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

        Report(AudioEditorExportStage.Preparing, 0.03d, "書き出しを準備しています...");
        if (!_coordinator.TryEnter(out var lease) || lease is null)
        {
            throw new InvalidOperationException("別の音声を書き出し中です。完了後にもう一度実行してください。");
        }

        using (lease)
        {
            if (File.Exists(outputFullPath) && await IsLibraryManagedAsync(outputFullPath, cancellationToken))
            {
                throw new InvalidOperationException("VoxArchiveライブラリに登録済みの音声ファイルは上書きできません。");
            }

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
            // 最終成果物と同じディレクトリへ置くことでFile.Replaceを同一ボリューム上で完結させる。
            var tempOutputPath = Path.Combine(outputDirectory ?? Path.GetTempPath(), $".{Path.GetFileNameWithoutExtension(outputFullPath)}.{Guid.NewGuid():N}.tmp{extension}");
            try
            {
                Report(AudioEditorExportStage.PeakAnalysis, 0.12d, "実ピークを解析しています...");
                var analysis = await AudioFileRenderService.AnalyzeAsync(inputFullPath, state, channelMode, cancellationToken);
                var assessment = AudioPeakAssessment.FromPeak(analysis.PeakAbsoluteSample);
                var masterGainDb = autoAttenuate ? assessment.RequiredMasterGainDb : 0d;

                Report(AudioEditorExportStage.Rendering, 0.38d, "編集内容をレンダリングしています...");
                var renderResult = await AudioFileRenderService.RenderWaveAsync(
                    inputFullPath,
                    tempWavePath,
                    state,
                    channelMode,
                    masterGainDb,
                    cancellationToken,
                    autoAttenuate: false);

                Report(AudioEditorExportStage.Encoding, 0.72d, $"{format} へ変換しています...");
                await ConvertWithFfmpegAsync(tempWavePath, tempOutputPath, format, ffmpegExecutablePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                Report(AudioEditorExportStage.Committing, 0.9d, "出力ファイルを確定しています...");
                CommitOutput(tempOutputPath, outputFullPath);
                var finalRenderResult = renderResult with
                {
                    AppliedMasterGainDb = masterGainDb,
                    AutoAttenuated = masterGainDb < -0.0000001d
                };

                if (format != AudioEditorExportFormat.Flac || !addFlacToLibrary)
                {
                    Report(AudioEditorExportStage.Completed, 1d, "書き出しが完了しました。");
                    return new AudioEditorExportResult(finalRenderResult, false, false, null);
                }

                try
                {
                    Report(AudioEditorExportStage.LibraryRegistration, 0.96d, "ライブラリへ登録しています...");
                    // UpdateTitleAsyncは未登録パスならCatalog Entryも作成するため、コピーや移動をせず出力先をそのまま登録できる。
                    await _catalogService.UpdateTitleAsync(outputFullPath, libraryTitle, cancellationToken);
                    Report(AudioEditorExportStage.Completed, 1d, "書き出しとライブラリ登録が完了しました。");
                    return new AudioEditorExportResult(finalRenderResult, true, true, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Export成功後のLibrary登録失敗で成果物を削除しない。両者は独立した結果として扱う。
                    Report(AudioEditorExportStage.Completed, 1d, "書き出しは完了しましたが、ライブラリ登録に失敗しました。");
                    return new AudioEditorExportResult(finalRenderResult, true, false, ex.Message);
                }
            }
            finally
            {
                TryDelete(tempWavePath);
                TryDelete(tempOutputPath);
            }
        }
    }

    private void Report(AudioEditorExportStage stage, double progress, string message)
        => ProgressChanged?.Invoke(this, new AudioEditorExportProgress(stage, Math.Clamp(progress, 0d, 1d), message));

    private async Task<bool> IsLibraryManagedAsync(string fullPath, CancellationToken cancellationToken)
    {
        var items = await _catalogService.GetAllAsync(cancellationToken);
        return items.Any(x => string.Equals(Path.GetFullPath(x.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void CommitOutput(string tempOutputPath, string outputFullPath)
    {
        if (File.Exists(outputFullPath))
        {
            File.Replace(tempOutputPath, outputFullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempOutputPath, outputFullPath);
        }
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
