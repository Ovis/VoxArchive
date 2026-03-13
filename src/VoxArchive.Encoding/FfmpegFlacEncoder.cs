using System.Diagnostics;
using VoxArchive.Encoding.Abstractions;

namespace VoxArchive.Encoding;

public sealed class FfmpegFlacEncoder : IFfmpegFlacEncoder
{
    private Process? _process;
    private Stream? _stdin;
    private readonly StringWriter _stderrBuffer = new();
    private Task? _stderrTask;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(FfmpegFlacEncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_process is not null)
        {
            throw new InvalidOperationException("Encoder already started.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputFilePath) ?? ".");

        var args = $"-f s16le -ar {options.SampleRate} -ac {options.Channels} -i - -c:a flac -compression_level {options.CompressionLevel} \"{options.OutputFilePath}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stderrTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _stderrBuffer.WriteLine(line);
                }
            }
        }, cancellationToken);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> pcm16StereoFrame, CancellationToken cancellationToken = default)
    {
        if (_stdin is null)
        {
            throw new InvalidOperationException("Encoder is not started.");
        }

        await _stdin.WriteAsync(pcm16StereoFrame, cancellationToken);
    }

    public async Task<FfmpegStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null)
        {
            return new FfmpegStopResult(0, true, string.Empty);
        }

        try
        {
            if (_stdin is not null)
            {
                await _stdin.FlushAsync(cancellationToken);
                await _stdin.DisposeAsync();
                _stdin = null;
            }

            await _process.WaitForExitAsync(cancellationToken);
            if (_stderrTask is not null)
            {
                await _stderrTask;
            }

            var exitCode = _process.ExitCode;
            var stderr = _stderrBuffer.ToString();
            var isSuccess = exitCode == 0;
            return new FfmpegStopResult(exitCode, isSuccess, stderr);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _stderrBuffer.Dispose();
    }
}
