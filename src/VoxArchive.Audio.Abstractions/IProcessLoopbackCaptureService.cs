namespace VoxArchive.Audio.Abstractions;

public interface IProcessLoopbackCaptureService
{
    event EventHandler<CaptureChunk>? ChunkCaptured;
    event EventHandler? TargetProcessExited;

    Task StartAsync(int targetProcessId, int sampleRate, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
