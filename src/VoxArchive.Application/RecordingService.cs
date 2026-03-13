using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Application;

public sealed class RecordingService : IRecordingService
{
    private readonly RecordingStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecordingState CurrentState => _stateMachine.CurrentState;

    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecordingStatistics>? StatisticsUpdated;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<OutputSourceChangedEvent>? OutputSourceChanged;

    public async Task<string> StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                throw new ArgumentException("OutputDirectory is required.", nameof(options));
            }

            TransitionTo(RecordingState.Starting);

            var fileName = DateTime.Now.ToString("yyyyMMddHHmmss") + ".flac";
            var outputPath = Path.Combine(options.OutputDirectory, fileName);

            TransitionTo(RecordingState.Recording);
            RaiseOutputSourceChanged(options.OutputCaptureMode, options.OutputCaptureMode, "RecordingStarted");
            RaiseStatistics(outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TransitionTo(RecordingState.Pausing);
            TransitionTo(RecordingState.Paused);
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TransitionTo(RecordingState.Recording);
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
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
            TransitionTo(RecordingState.Stopping);
            TransitionTo(RecordingState.Stopped);
            RaiseStatistics(outputFilePath: null);
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TransitionTo(RecordingState next)
    {
        _stateMachine.Transition(next);
        StateChanged?.Invoke(this, next);
    }

    private void SafeTransitionToError(string message)
    {
        if (_stateMachine.TryTransition(RecordingState.Error, out _))
        {
            StateChanged?.Invoke(this, RecordingState.Error);
        }

        ErrorOccurred?.Invoke(this, message);
    }

    private void RaiseStatistics(string? outputFilePath)
    {
        StatisticsUpdated?.Invoke(this, new RecordingStatistics
        {
            ElapsedTime = TimeSpan.Zero,
            OutputFilePath = outputFilePath
        });
    }

    private void RaiseOutputSourceChanged(OutputCaptureMode previous, OutputCaptureMode current, string reason)
    {
        OutputSourceChanged?.Invoke(this, new OutputSourceChangedEvent(previous, current, reason, DateTimeOffset.Now));
    }
}
