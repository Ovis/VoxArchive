using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class TranscriptionJobQueue : IDisposable
{
    private readonly Lock _stateGate = new();
    private readonly WhisperTranscriptionService _transcriptionService;
    private readonly ILogger<TranscriptionJobQueue> _logger;
    private readonly Channel<TranscriptionJobRequest> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly ConcurrentDictionary<string, TranscriptionJobState> _jobStates = new(StringComparer.OrdinalIgnoreCase);

    public TranscriptionJobQueue(WhisperTranscriptionService transcriptionService, ILogger<TranscriptionJobQueue> logger)
    {
        _transcriptionService = transcriptionService;
        _logger = logger;
        _queue = Channel.CreateUnbounded<TranscriptionJobRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(WorkerLoopAsync);
    }

    public event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;
    public event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;

    public bool TryEnqueue(TranscriptionJobRequest request)
    {
        var key = NormalizePathKey(request.AudioFilePath);
        lock (_stateGate)
        {
            if (_jobStates.ContainsKey(key))
            {
                return false;
            }

            if (!_queue.Writer.TryWrite(request))
            {
                return false;
            }

            _jobStates[key] = TranscriptionJobState.Pending;
        }

        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(request.AudioFilePath, TranscriptionJobState.Pending));
        return true;
    }

    public IReadOnlyCollection<TranscriptionJobStateSnapshot> GetStateSnapshot()
    {
        return _jobStates
            .Select(kvp => new TranscriptionJobStateSnapshot(kvp.Key, kvp.Value))
            .ToArray();
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_queue.Reader.TryRead(out var request))
                {
                    SetJobState(request.AudioFilePath, TranscriptionJobState.Running);
                    var result = await ProcessAsync(request, _cts.Token);
                    ClearJobState(request.AudioFilePath);
                    JobCompleted?.Invoke(this, new TranscriptionJobCompletedEventArgs(request, result));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Transcription job worker canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription job worker loop failed.");
        }
    }

    private async Task<TranscriptionJobResult> ProcessAsync(TranscriptionJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var priority = request.Trigger == TranscriptionTrigger.AutoAfterRecord
                ? request.Options.AutoTranscriptionPriority
                : request.Options.ManualTranscriptionPriority;

            if (priority == TranscriptionPriority.Low)
            {
                await Task.Delay(300, cancellationToken);
            }

            return await _transcriptionService.TranscribeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionJobResult(
                Succeeded: false,
                Message: "文字起こし処理がキャンセルされました。",
                GeneratedFiles: Array.Empty<string>(),
                StartedAt: DateTimeOffset.Now,
                FinishedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new TranscriptionJobResult(
                Succeeded: false,
                Message: $"文字起こし実行中に例外が発生しました: {ex.Message}",
                GeneratedFiles: Array.Empty<string>(),
                StartedAt: DateTimeOffset.Now,
                FinishedAt: DateTimeOffset.Now);
        }
    }

    private void SetJobState(string audioFilePath, TranscriptionJobState state)
    {
        var key = NormalizePathKey(audioFilePath);
        _jobStates[key] = state;
        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(audioFilePath, state));
    }

    private void ClearJobState(string audioFilePath)
    {
        var key = NormalizePathKey(audioFilePath);
        _jobStates.TryRemove(key, out _);
        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(audioFilePath, null));
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path).Trim();
        }
        catch
        {
            return path.Trim();
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _ = _workerTask.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                _logger.LogDebug(task.Exception, "Transcription job worker ended with fault during dispose.");
            }
        }, TaskScheduler.Default);


        _cts.Dispose();
    }
}

public sealed record TranscriptionJobCompletedEventArgs(
    TranscriptionJobRequest Request,
    TranscriptionJobResult Result);

public enum TranscriptionJobState
{
    Pending,
    Running
}

public sealed record TranscriptionJobStateSnapshot(string AudioFilePath, TranscriptionJobState State);

public sealed record TranscriptionJobStateChangedEventArgs(string AudioFilePath, TranscriptionJobState? State);

