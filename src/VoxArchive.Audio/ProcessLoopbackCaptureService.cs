using System.Diagnostics;
using System.Reflection;
using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class ProcessLoopbackCaptureService : IProcessLoopbackCaptureService
{
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _targetProcessId;
    private bool _running;

    private object? _capture;
    private EventInfo? _dataAvailableEvent;
    private Delegate? _dataAvailableHandler;
    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private bool _isFloat;

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

        _capture = TryCreateProcessLoopbackCapture(targetProcessId);
        if (_capture is not null)
        {
            _sampleRate = NaudioCaptureUtils.ResolveSampleRate(_capture);
            (_channels, _bitsPerSample, _isFloat) = NaudioCaptureUtils.ResolveFormat(_capture);

            _dataAvailableEvent = _capture.GetType().GetEvent("DataAvailable");
            _dataAvailableHandler = NaudioCaptureUtils.CreateDataAvailableDelegate(this, _dataAvailableEvent!, nameof(OnDataAvailable));
            _dataAvailableEvent.AddEventHandler(_capture, _dataAvailableHandler);
            NaudioCaptureUtils.StartRecording(_capture);
        }

        _monitorCts?.Cancel();
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorTargetProcessAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;

        if (_capture is not null)
        {
            if (_dataAvailableEvent is not null && _dataAvailableHandler is not null)
            {
                _dataAvailableEvent.RemoveEventHandler(_capture, _dataAvailableHandler);
            }

            NaudioCaptureUtils.StopRecording(_capture);
            NaudioCaptureUtils.DisposeCapture(_capture);
            _capture = null;
            _dataAvailableEvent = null;
            _dataAvailableHandler = null;
        }

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

    private static object? TryCreateProcessLoopbackCapture(int processId)
    {
        var captureType = Type.GetType("NAudio.Wave.WasapiProcessLoopbackCapture, NAudio", throwOnError: false);
        if (captureType is null)
        {
            return null;
        }

        foreach (var ctor in captureType.GetConstructors())
        {
            var ps = ctor.GetParameters();
            try
            {
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                {
                    return ctor.Invoke(new object[] { processId });
                }

                if (ps.Length == 1 && ps[0].ParameterType == typeof(uint))
                {
                    return ctor.Invoke(new object[] { (uint)processId });
                }

                if (ps.Length == 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(bool))
                {
                    return ctor.Invoke(new object[] { processId, true });
                }

                if (ps.Length == 2 && ps[0].ParameterType == typeof(uint) && ps[1].ParameterType == typeof(bool))
                {
                    return ctor.Invoke(new object[] { (uint)processId, true });
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private void OnDataAvailable(object? sender, object args)
    {
        if (!_running)
        {
            return;
        }

        var argsType = args.GetType();
        var buffer = (byte[]?)argsType.GetProperty("Buffer")?.GetValue(args);
        var bytesRecorded = (int?)argsType.GetProperty("BytesRecorded")?.GetValue(args) ?? 0;

        if (buffer is null || bytesRecorded <= 0)
        {
            return;
        }

        var mono = NaudioCaptureUtils.ToMonoFloat(buffer, bytesRecorded, _channels, _bitsPerSample, _isFloat);
        if (mono.Length == 0)
        {
            return;
        }

        ChunkCaptured?.Invoke(this, new CaptureChunk(mono, _sampleRate, DateTimeOffset.UtcNow));
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

