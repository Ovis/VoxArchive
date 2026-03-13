using System.Diagnostics;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Audio;

public sealed class OutputCaptureFailoverCoordinator : IOutputCaptureFailoverCoordinator
{
    private readonly IOutputCaptureController _controller;
    private OutputCaptureMode _currentMode = OutputCaptureMode.SpeakerLoopback;

    public OutputCaptureFailoverCoordinator(IOutputCaptureController controller)
    {
        _controller = controller;
        _controller.SourceChanged += (_, e) =>
        {
            _currentMode = e.Current;
            SourceChanged?.Invoke(this, e);
        };
    }

    public event EventHandler<OutputSourceChangedEvent>? SourceChanged;

    public Task<OutputCaptureMode> ResolveStartupModeAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        if (options.OutputCaptureMode != OutputCaptureMode.ProcessLoopback)
        {
            _currentMode = OutputCaptureMode.SpeakerLoopback;
            return Task.FromResult(OutputCaptureMode.SpeakerLoopback);
        }

        if (options.TargetProcessId is not int pid || !IsProcessAlive(pid))
        {
            var ev = new OutputSourceChangedEvent(
                OutputCaptureMode.ProcessLoopback,
                OutputCaptureMode.SpeakerLoopback,
                "TargetProcessNotRunningAtStart",
                DateTimeOffset.UtcNow);
            _currentMode = OutputCaptureMode.SpeakerLoopback;
            SourceChanged?.Invoke(this, ev);
            return Task.FromResult(OutputCaptureMode.SpeakerLoopback);
        }

        _currentMode = OutputCaptureMode.ProcessLoopback;
        return Task.FromResult(OutputCaptureMode.ProcessLoopback);
    }

    public async Task HandleProcessCaptureUnavailableAsync(CancellationToken cancellationToken = default)
    {
        if (_currentMode != OutputCaptureMode.ProcessLoopback)
        {
            return;
        }

        await _controller.SwitchToSpeakerLoopbackAsync("TargetProcessExited", cancellationToken);
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
