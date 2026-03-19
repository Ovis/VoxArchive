using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Audio;

public sealed class OutputCaptureController : IOutputCaptureController
{
    private readonly IOutputCaptureSource _speakerSource;
    private readonly IOutputCaptureSource _processSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOutputCaptureSource? _activeSource;
    private RecordingOptions? _lastOptions;

    public OutputCaptureController(IOutputCaptureSource speakerSource, IOutputCaptureSource processSource)
    {
        _speakerSource = speakerSource;
        _processSource = processSource;

        _speakerSource.ChunkCaptured += OnChunkCaptured;
        _processSource.ChunkCaptured += OnChunkCaptured;

        _speakerSource.SourceUnavailable += OnSourceUnavailable;
        _processSource.SourceUnavailable += OnSourceUnavailable;
    }

    public OutputCaptureMode CurrentMode => _activeSource?.Mode ?? OutputCaptureMode.SpeakerLoopback;

    public event EventHandler<CaptureChunk>? ChunkCaptured;
    public event EventHandler<OutputSourceChangedEvent>? SourceChanged;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _lastOptions = options;
            var next = options.OutputCaptureMode == OutputCaptureMode.ProcessLoopback ? _processSource : _speakerSource;

            try
            {
                await StartSourceAsync(next, options, "Start", cancellationToken);
            }
            catch (Exception ex) when (ReferenceEquals(next, _processSource))
            {
                var speakerOptions = options with
                {
                    OutputCaptureMode = OutputCaptureMode.SpeakerLoopback,
                    TargetProcessId = null
                };
                _lastOptions = speakerOptions;
                var reason = string.IsNullOrWhiteSpace(ex.Message)
                    ? "ProcessStartFailedFallback"
                    : $"ProcessStartFailedFallback:{ex.Message}";
                await StartSourceAsync(_speakerSource, speakerOptions, reason, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SwitchToSpeakerLoopbackAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastOptions is null)
            {
                return;
            }

            var speakerOptions = _lastOptions with
            {
                OutputCaptureMode = OutputCaptureMode.SpeakerLoopback,
                TargetProcessId = null
            };

            _lastOptions = speakerOptions;
            await StartSourceAsync(_speakerSource, speakerOptions, reason, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSource is not null)
            {
                await _activeSource.StopAsync(cancellationToken);
                _activeSource = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartSourceAsync(IOutputCaptureSource nextSource, RecordingOptions options, string reason, CancellationToken cancellationToken)
    {
        var previousMode = _activeSource?.Mode ?? nextSource.Mode;

        if (_activeSource is not null)
        {
            await _activeSource.StopAsync(cancellationToken);
        }

        _activeSource = nextSource;
        await _activeSource.StartAsync(options, cancellationToken);

        if (previousMode != _activeSource.Mode || reason != "Start")
        {
            SourceChanged?.Invoke(this, new OutputSourceChangedEvent(previousMode, _activeSource.Mode, reason, DateTimeOffset.UtcNow));
        }
    }

    private void OnChunkCaptured(object? sender, CaptureChunk chunk)
    {
        if (_activeSource is null)
        {
            return;
        }

        if (ReferenceEquals(sender, _activeSource))
        {
            ChunkCaptured?.Invoke(this, chunk);
        }
    }

    private void OnSourceUnavailable(object? sender, EventArgs eventArgs)
    {
        if (_activeSource?.Mode == OutputCaptureMode.ProcessLoopback)
        {
            _ = SwitchToSpeakerLoopbackAsync("ProcessSourceUnavailable");
        }
    }
}

