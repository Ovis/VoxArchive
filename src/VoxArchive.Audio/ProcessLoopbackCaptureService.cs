using System.Diagnostics;
using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class ProcessLoopbackCaptureService : IProcessLoopbackCaptureService
{
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _targetProcessId;
    private bool _running;

    public event EventHandler<CaptureChunk>? ChunkCaptured;
    public event EventHandler? TargetProcessExited;

    public Task StartAsync(int targetProcessId, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (targetProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetProcessId));
        }

        _targetProcessId = targetProcessId;
        _running = true;
        _monitorCts?.Cancel();
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorTargetProcessAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync();
            _monitorCts.Dispose();
            _monitorCts = null;
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            _monitorTask = null;
        }
    }

    public void PublishTestChunk(ReadOnlyMemory<float> samples, int sampleRate)
    {
        if (!_running)
        {
            return;
        }

        ChunkCaptured?.Invoke(this, new CaptureChunk(samples, sampleRate, DateTimeOffset.UtcNow));
    }

    private async Task MonitorTargetProcessAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_running)
                {
                    break;
                }

                if (!IsProcessAlive(_targetProcessId))
                {
                    _running = false;
                    TargetProcessExited?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
