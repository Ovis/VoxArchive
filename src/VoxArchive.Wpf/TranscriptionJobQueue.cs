using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoxArchive.Domain;
using Whisper.net.Logger;

namespace VoxArchive.Wpf;

public sealed class TranscriptionJobQueue : IDisposable
{
    private readonly Lock _stateGate = new();
    private readonly WhisperTranscriptionService _transcriptionService;
    private readonly ILogger<TranscriptionJobQueue> _logger;
    private readonly Channel<TranscriptionJobRequest> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly IDisposable _whisperLogSubscription;
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

        // Whisper.net のネイティブランタイム選択や CUDA 初期化結果を通常のアプリログへ取り込む。
        // アプリ全体の最小ログレベルが Information のため、Whisper.net の Debug も Information として記録する。
        _whisperLogSubscription = LogProvider.AddLogger((level, message) =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var normalizedMessage = message.Trim();
            switch (level)
            {
                case WhisperLogLevel.Error:
                    _logger.LogError("Whisper.net native [{WhisperLevel}]: {Message}", level, normalizedMessage);
                    break;
                case WhisperLogLevel.Warning:
                    _logger.LogWarning("Whisper.net native [{WhisperLevel}]: {Message}", level, normalizedMessage);
                    break;
                default:
                    _logger.LogInformation("Whisper.net native [{WhisperLevel}]: {Message}", level, normalizedMessage);
                    break;
            }
        });

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
                _logger.LogInformation("Transcription job enqueue skipped because the file is already pending or running. File={File}", request.AudioFilePath);
                return false;
            }

            if (!_queue.Writer.TryWrite(request))
            {
                _logger.LogWarning("Transcription job enqueue failed because the queue writer rejected the request. File={File}", request.AudioFilePath);
                return false;
            }

            _jobStates[key] = TranscriptionJobState.Pending;
        }

        _logger.LogInformation(
            "Transcription job queued. File={File}, Trigger={Trigger}, ExecutionMode={ExecutionMode}, Model={Model}, Language={Language}, OutputFormats={OutputFormats}",
            request.AudioFilePath,
            request.Trigger,
            request.Options.TranscriptionExecutionMode,
            request.Options.TranscriptionModel,
            request.Options.TranscriptionLanguage,
            request.Options.TranscriptionOutputFormats);

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
        var stopwatch = Stopwatch.StartNew();
        var priority = request.Trigger == TranscriptionTrigger.AutoAfterRecord
            ? request.Options.AutoTranscriptionPriority
            : request.Options.ManualTranscriptionPriority;

        _logger.LogInformation(
            "Transcription job started. File={File}, Trigger={Trigger}, ExecutionMode={ExecutionMode}, Model={Model}, Language={Language}, OutputFormats={OutputFormats}, Priority={Priority}",
            request.AudioFilePath,
            request.Trigger,
            request.Options.TranscriptionExecutionMode,
            request.Options.TranscriptionModel,
            request.Options.TranscriptionLanguage,
            request.Options.TranscriptionOutputFormats,
            priority);

        try
        {
            if (priority == TranscriptionPriority.Low)
            {
                await Task.Delay(300, cancellationToken);
            }

            var result = await _transcriptionService.TranscribeAsync(request, cancellationToken);
            stopwatch.Stop();

            _logger.LogInformation(
                "Transcription job finished. File={File}, Succeeded={Succeeded}, ElapsedMs={ElapsedMs}, GeneratedFileCount={GeneratedFileCount}, Message={Message}",
                request.AudioFilePath,
                result.Succeeded,
                stopwatch.ElapsedMilliseconds,
                result.GeneratedFiles.Count,
                result.Message);

            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Transcription job canceled. File={File}, ElapsedMs={ElapsedMs}",
                request.AudioFilePath,
                stopwatch.ElapsedMilliseconds);

            return new TranscriptionJobResult(
                Succeeded: false,
                Message: "文字起こし処理がキャンセルされました。",
                GeneratedFiles: Array.Empty<string>(),
                StartedAt: DateTimeOffset.Now,
                FinishedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Transcription job failed with an exception. File={File}, ElapsedMs={ElapsedMs}",
                request.AudioFilePath,
                stopwatch.ElapsedMilliseconds);

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

        _whisperLogSubscription.Dispose();
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
