using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// Admission済み文字起こしジョブを優先度順に逐次実行し、待機中・実行中の状態を管理する
/// </summary>
/// <remarks>
/// QueueはEngine解決、settings解釈、model readiness、artifact生成を行わない。
/// それらはAdmissionとCommon Orchestratorへ委譲し、workerはimmutable snapshotを順番に実行するだけに限定する。
/// </remarks>
public sealed class TranscriptionJobQueue : IDisposable
{
    private readonly object _stateGate = new();
    private readonly TranscriptionJobAdmissionService _admissionService;
    private readonly TranscriptionOrchestrator _orchestrator;
    private readonly ILogger<TranscriptionJobQueue> _logger;
    private readonly PriorityQueue<QueuedTranscriptionJob, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ConcurrentDictionary<string, TranscriptionJobState> _jobStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, QueuedTranscriptionJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;
    private int _disposed;

    public TranscriptionJobQueue(
        TranscriptionJobAdmissionService admissionService,
        TranscriptionOrchestrator orchestrator,
        ILogger<TranscriptionJobQueue> logger)
    {
        _admissionService = admissionService;
        _orchestrator = orchestrator;
        _logger = logger;
        _workerTask = Task.Run(WorkerLoopAsync);
    }

    public event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;
    public event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;

    /// <summary>
    /// 現在の設定をAdmissionで確定し、同一録音が未投入の場合だけQueueへ追加する
    /// </summary>
    public async Task<TranscriptionQueueEnqueueResult> TryEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = NormalizePathKey(audioFilePath);
        lock (_stateGate)
        {
            if (_jobStates.ContainsKey(key))
            {
                return new TranscriptionQueueEnqueueResult(false, "この録音は既に文字起こし待機中または実行中です。", null);
            }
            _jobStates[key] = TranscriptionJobState.Pending;
        }

        TranscriptionAdmissionResult admission;
        try
        {
            admission = await _admissionService.AdmitAsync(audioFilePath, recordingOptions, trigger, cancellationToken);
        }
        catch
        {
            ClearStateOnly(audioFilePath);
            throw;
        }

        if (!admission.Succeeded || admission.Job is null)
        {
            ClearStateOnly(audioFilePath);
            return new TranscriptionQueueEnqueueResult(false, admission.Message, admission.MissingModel);
        }

        var queued = new QueuedTranscriptionJob(admission.Job, new CancellationTokenSource());
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _jobStates.TryRemove(key, out _);
                queued.Dispose();
                return new TranscriptionQueueEnqueueResult(false, "文字起こしQueueは終了済みです。", null);
            }

            _jobs[key] = queued;
            var sequence = Interlocked.Increment(ref _sequence);
            // PriorityQueueは小さい値を先に取り出すため、Normal(1)をLow(0)より先にする。
            // 同一優先度ではsequenceを使いFIFOを維持する。
            _queue.Enqueue(queued, (-(int)queued.Job.Priority, sequence));
        }
        _queueSignal.Release();

        if (queued.Job.Descriptor.DiagnosticsEnabled)
        {
            _logger.LogInformation(
                "Transcription job queued. File={File}, Trigger={Trigger}, Engine={Engine}, Model={Model}, Priority={Priority}",
                queued.Job.Descriptor.AudioFilePath,
                queued.Job.Descriptor.Trigger,
                queued.Job.Descriptor.EngineId,
                queued.Job.Descriptor.ModelId,
                queued.Job.Priority);
        }

        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(audioFilePath, TranscriptionJobState.Pending));
        return new TranscriptionQueueEnqueueResult(true, "文字起こしをキューへ追加しました。", null);
    }

    /// <summary>
    /// 待機中または実行中の指定録音ジョブへキャンセルを要求する
    /// </summary>
    /// <remarks>
    /// native ASRが即時停止できない場合でもthread abortは行わず、Engine/Commonのsafe boundaryでCancellationTokenを観測する。
    /// </remarks>
    public bool Cancel(string audioFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        if (!_jobs.TryGetValue(NormalizePathKey(audioFilePath), out var queued))
        {
            return false;
        }

        if (!queued.Cancellation.IsCancellationRequested)
        {
            queued.Cancellation.Cancel();
        }
        return true;
    }

    /// <summary>待機中・実行中ジョブの状態一覧を取得する</summary>
    public IReadOnlyCollection<TranscriptionJobStateSnapshot> GetStateSnapshot()
        => _jobStates.Select(x => new TranscriptionJobStateSnapshot(x.Key, x.Value)).ToArray();

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                await _queueSignal.WaitAsync(_cts.Token);

                QueuedTranscriptionJob? queued = null;
                lock (_stateGate)
                {
                    if (_queue.Count > 0)
                    {
                        queued = _queue.Dequeue();
                    }
                }

                if (queued is null)
                {
                    continue;
                }

                TranscriptionJobResult result;
                if (queued.Cancellation.IsCancellationRequested)
                {
                    var now = DateTimeOffset.Now;
                    result = new TranscriptionJobResult(
                        false,
                        "文字起こし処理がキャンセルされました。",
                        Array.Empty<string>(),
                        now,
                        now)
                    {
                        Outcome = TranscriptionJobOutcome.Cancelled
                    };
                }
                else
                {
                    SetJobState(queued.Job.Descriptor.AudioFilePath, TranscriptionJobState.Running);
                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _cts.Token,
                        queued.Cancellation.Token);
                    result = await ProcessAsync(queued.Job, linkedCancellation.Token);
                }

                ClearJob(queued.Job.Descriptor.AudioFilePath);
                JobCompleted?.Invoke(this, new TranscriptionJobCompletedEventArgs(queued.Job.Descriptor, result));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _logger.LogDebug("Transcription job worker canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription job worker loop failed.");
        }
    }

    private async Task<TranscriptionJobResult> ProcessAsync(AdmittedTranscriptionJob job, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (job.Descriptor.DiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Transcription job started. File={File}, Engine={Engine}, Model={Model}",
                    job.Descriptor.AudioFilePath,
                    job.Descriptor.EngineId,
                    job.Descriptor.ModelId);
            }

            var result = await _orchestrator.TranscribeAsync(job.Request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            if (job.Descriptor.DiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Transcription job finished. File={File}, ElapsedMs={ElapsedMs}, GeneratedFileCount={GeneratedFileCount}",
                    job.Descriptor.AudioFilePath,
                    stopwatch.ElapsedMilliseconds,
                    result.GeneratedFiles.Count);
            }

            return new TranscriptionJobResult(
                true,
                "文字起こしが完了しました。",
                result.GeneratedFiles,
                startedAt,
                result.FinishedAt)
            {
                Outcome = TranscriptionJobOutcome.Completed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TranscriptionJobResult(
                false,
                "文字起こし処理がキャンセルされました。",
                Array.Empty<string>(),
                startedAt,
                DateTimeOffset.Now)
            {
                Outcome = TranscriptionJobOutcome.Cancelled
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Transcription job failed. File={File}, ElapsedMs={ElapsedMs}", job.Descriptor.AudioFilePath, stopwatch.ElapsedMilliseconds);
            return new TranscriptionJobResult(
                false,
                $"文字起こし実行中に例外が発生しました: {ex.Message}",
                Array.Empty<string>(),
                startedAt,
                DateTimeOffset.Now)
            {
                Outcome = TranscriptionJobOutcome.Failed
            };
        }
    }

    private void SetJobState(string path, TranscriptionJobState state)
    {
        _jobStates[NormalizePathKey(path)] = state;
        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(path, state));
    }

    private void ClearJob(string path)
    {
        var key = NormalizePathKey(path);
        _jobStates.TryRemove(key, out _);
        if (_jobs.TryRemove(key, out var queued)) queued.Dispose();
        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(path, null));
    }

    private void ClearStateOnly(string path)
    {
        _jobStates.TryRemove(NormalizePathKey(path), out _);
        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(path, null));
    }

    private static string NormalizePathKey(string path)
    {
        try { return Path.GetFullPath(path).Trim(); }
        catch { return path.Trim(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        lock (_stateGate)
        {
            foreach (var queued in _jobs.Values)
            {
                if (!queued.Cancellation.IsCancellationRequested)
                {
                    queued.Cancellation.Cancel();
                }
                queued.Job.Dispose();
            }
            _jobs.Clear();
            _jobStates.Clear();
            _queue.Clear();
        }

        _ = _workerTask.ContinueWith(task =>
        {
            if (task.IsFaulted) _logger.LogDebug(task.Exception, "Transcription worker ended with fault during dispose.");
        }, TaskScheduler.Default);

        // workerがnative ASRのsafe boundaryから戻る前にCTSをDisposeすると、
        // linked token生成・観測と競合し得るためプロセス終了時の小さなresourceは明示Disposeしない。
    }

    private sealed class QueuedTranscriptionJob(
        AdmittedTranscriptionJob job,
        CancellationTokenSource cancellation) : IDisposable
    {
        public AdmittedTranscriptionJob Job { get; } = job;
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Dispose()
        {
            Job.Dispose();
            Cancellation.Dispose();
        }
    }
}

/// <summary>Application内部Queueの投入結果を表す</summary>
public sealed record TranscriptionQueueEnqueueResult(
    bool Enqueued,
    string Message,
    TranscriptionMissingModelInfo? MissingModel);
