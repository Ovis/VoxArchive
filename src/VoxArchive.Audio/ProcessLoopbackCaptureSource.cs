using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Audio;

public sealed class ProcessLoopbackCaptureSource : IOutputCaptureSource
{
    private readonly IProcessLoopbackCaptureService _processCaptureService;

    public ProcessLoopbackCaptureSource(IProcessLoopbackCaptureService processCaptureService)
    {
        _processCaptureService = processCaptureService;
        _processCaptureService.ChunkCaptured += OnChunkCaptured;
        _processCaptureService.TargetProcessExited += OnTargetProcessExited;
    }

    public OutputCaptureMode Mode => OutputCaptureMode.ProcessLoopback;

    public event EventHandler<CaptureChunk>? ChunkCaptured;
    public event EventHandler? SourceUnavailable;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        if (options.TargetProcessId is null)
        {
            throw new InvalidOperationException("TargetProcessId is required for process loopback mode.");
        }

        await _processCaptureService.StartAsync(options.TargetProcessId.Value, options.SampleRate, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _processCaptureService.StopAsync(cancellationToken);
    }

    private void OnChunkCaptured(object? sender, CaptureChunk chunk)
    {
        ChunkCaptured?.Invoke(this, chunk);
    }

    private void OnTargetProcessExited(object? sender, EventArgs eventArgs)
    {
        SourceUnavailable?.Invoke(this, EventArgs.Empty);
    }
}
