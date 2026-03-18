using System.Reflection;
using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class NAudioSpeakerCaptureService : ISpeakerCaptureService
{
    private object? _capture;
    private EventInfo? _dataAvailableEvent;
    private Delegate? _dataAvailableHandler;
    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private bool _isFloat;

    public event EventHandler<CaptureChunk>? ChunkCaptured;

    public Task StartAsync(string speakerDeviceId, int sampleRate, CancellationToken cancellationToken = default)
    {
        _capture = NAudioCaptureUtils.CreateCapture("NAudio.Wave.WasapiLoopbackCapture, NAudio.Wasapi", speakerDeviceId);
        _sampleRate = NAudioCaptureUtils.ResolveSampleRate(_capture);
        (_channels, _bitsPerSample, _isFloat) = NAudioCaptureUtils.ResolveFormat(_capture);

        _dataAvailableEvent = _capture.GetType().GetEvent("DataAvailable")
            ?? throw new InvalidOperationException("DataAvailable event was not found.");
        _dataAvailableHandler = NAudioCaptureUtils.CreateDataAvailableDelegate(this, _dataAvailableEvent, nameof(OnDataAvailable));
        _dataAvailableEvent.AddEventHandler(_capture, _dataAvailableHandler);

        NAudioCaptureUtils.StartRecording(_capture);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null)
        {
            return Task.CompletedTask;
        }

        if (_dataAvailableEvent is not null && _dataAvailableHandler is not null)
        {
            _dataAvailableEvent.RemoveEventHandler(_capture, _dataAvailableHandler);
        }

        NAudioCaptureUtils.StopRecording(_capture);
        NAudioCaptureUtils.DisposeCapture(_capture);
        _capture = null;
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, object args)
    {
        var argsType = args.GetType();
        var buffer = (byte[]?)argsType.GetProperty("Buffer")?.GetValue(args);
        var bytesRecorded = (int?)argsType.GetProperty("BytesRecorded")?.GetValue(args) ?? 0;

        if (buffer is null || bytesRecorded <= 0)
        {
            return;
        }

        var mono = NAudioCaptureUtils.ToMonoFloat(buffer, bytesRecorded, _channels, _bitsPerSample, _isFloat);
        if (mono.Length == 0)
        {
            return;
        }

        ChunkCaptured?.Invoke(this, new CaptureChunk(mono, _sampleRate, DateTimeOffset.UtcNow));
    }
}
