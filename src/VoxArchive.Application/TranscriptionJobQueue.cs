using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// Admission済み文字起こしジョブを逐次実行し、待機中・実行中の状態を管理する
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
    private readonly Channel<AdmittedTranscriptionJob> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ConcurrentDictionary<string, TranscriptionJobState> _jobStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AdmittedTranscriptionJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public TranscriptionJobQueue(
        TranscriptionJobAdmissionService admissionService,
        TranscriptionOrchestrator orchestrator,
        ILogger<TranscriptionJobQueue> logger)
    {
        _admissionService = admissionService;
        _orchestrator = orchestrator;
        _logger = logger;
        _queue = Channel.CreateUnbounded<AdmittedTranscriptionJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
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

        var job = admission.Job;
        lock (_stateGate)
        {
            _jobs[key] = job;
            if (!_queue.Writer.TryWrite(job))
            {
                _jobs.TryRemove(key, out _);
                _jobStates.TryRemove(key, out _);
                job.Dispose();
                return new TranscriptionQueueEnqueueResult(false, "文字起こしQueueへ追加できませんでした。", null);
            }
        }

        if (job.Descriptor.DiagnosticsEnabled)
        {
            _logger.LogInformation(
                "Transcription job queued. File={File}, Trigger={Trigger}, Engine={Engine}, Model={Model}, Priority={Priority}",
                job.Descriptor.AudioFilePath,
                job.Descriptor.Trigger,
                job.Descriptor.EngineId,
                job.Descriptor.ModelId,
                job.Priority);
        }

        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(audioFilePath, TranscriptionJobState.Pending));
        return new TranscriptionQueueEnqueueResult(true, "文字起こしをキューへ追加しました。", null);
    }

    /// <summary>待機中・実行中ジョブの状態一覧を取得する</summary>
    public IReadOnlyCollection<TranscriptionJobStateSnapshot> GetStateSnapshot()
        => _jobStates.Select(x => new TranscriptionJobStateSnapshot(x.Key, x.Value)).ToArray();

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_queue.Reader.TryRead(out var job))
                {
                    SetJobState(job.Descriptor.AudioFilePath, TranscriptionJobState.Running);
                    var result = await ProcessAsync(job, _cts.Token);
                    ClearJob(job.Descriptor.AudioFilePath);
                    JobCompleted?.Invoke(this, new TranscriptionJobCompletedEventArgs(job.Descriptor, result));
                }
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
            if (job.Priority == TranscriptionPriority.Low)
            {
                await Task.Delay(300, cancellationToken);
            }

            if (job.Descriptor.DiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Transcription job started. File={File}, Engine={Engine}, Model={Model}",
                    job.Descriptor.AudioFilePath,
                    job.Descriptor.EngineId,
                    job.Descriptor.ModelId);
            }

            var result = await _orchestrator.TranscribeAsync(job.Request, cancellationToken);
            stopwatch.Stop();
            if (job.Descriptor.DiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Transcription job finished. File={File}, ElapsedMs={ElapsedMs}, GeneratedFileCount={GeneratedFileCount}",
                    job.Descriptor.AudioFilePath,
                    stopwatch.ElapsedMilliseconds,
                    result.GeneratedFiles.Count);
            }

            return new TranscriptionJobResult(true, "文字起こしが完了しました。", result.GeneratedFiles, startedAt, result.FinishedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TranscriptionJobResult(false, "文字起こし処理がキャンセルされました。", Array.Empty<string>(), startedAt, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Transcription job failed. File={File}, ElapsedMs={ElapsedMs}", job.Descriptor.AudioFilePath, stopwatch.ElapsedMilliseconds);
            return new TranscriptionJobResult(false, $"文字起こし実行中に例外が発生しました: {ex.Message}", Array.Empty<string>(), startedAt, DateTimeOffset.Now);
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
        if (_jobs.TryRemove(key, out var job)) job.Dispose();
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

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        lock (_stateGate)
        {
            foreach (var job in _jobs.Values) job.Dispose();
            _jobs.Clear();
            _jobStates.Clear();
        }
        _ = _workerTask.ContinueWith(task =>
        {
            if (task.IsFaulted) _logger.LogDebug(task.Exception, "Transcription worker ended with fault during dispose.");
        }, TaskScheduler.Default);
        _cts.Dispose();
    }
}

/// <summary>Application内部Queueの投入結果を表す</summary>
public sealed record TranscriptionQueueEnqueueResult(
    bool Enqueued,
    string Message,
    TranscriptionMissingModelInfo? MissingModel);
