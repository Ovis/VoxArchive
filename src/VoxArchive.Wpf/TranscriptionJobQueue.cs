using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoxArchive.Domain;
using Whisper.net.Logger;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしジョブを逐次実行し、待機中・実行中の状態を管理する
/// </summary>
public sealed class TranscriptionJobQueue : IDisposable
{
    private readonly Lock _stateGate = new();
    private readonly TranscriptionOrchestrator _orchestrator;
    private readonly TranscriptionModelRequirementService _modelRequirementService;
    private readonly TranscriptionModelUsageTracker _modelUsageTracker;
    private readonly ILogger<TranscriptionJobQueue> _logger;
    private readonly Channel<TranscriptionJobRequest> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly IDisposable _whisperLogSubscription;
    private readonly ConcurrentDictionary<string, TranscriptionJobState> _jobStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TranscriptionJobRequest> _jobRequests = new(StringComparer.OrdinalIgnoreCase);
    private int _transcriptionDiagnosticsActive;

    /// <summary>
    /// Queueを初期化し、バックグラウンドの逐次実行Workerを開始する
    /// </summary>
    public TranscriptionJobQueue(
        TranscriptionOrchestrator orchestrator,
        TranscriptionModelRequirementService modelRequirementService,
        TranscriptionModelUsageTracker modelUsageTracker,
        ILogger<TranscriptionJobQueue> logger)
    {
        _orchestrator = orchestrator;
        _modelRequirementService = modelRequirementService;
        _modelUsageTracker = modelUsageTracker;
        _logger = logger;
        _queue = Channel.CreateUnbounded<TranscriptionJobRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();

        // Whisper.net のネイティブログは診断ログが有効な文字起こしジョブの実行中だけ取り込む。
        // Engine抽象化後も既存の診断挙動を変えないため、このPRではログ購読をQueueに残す。
        _whisperLogSubscription = LogProvider.AddLogger((level, message) =>
        {
            if (Volatile.Read(ref _transcriptionDiagnosticsActive) == 0 || string.IsNullOrWhiteSpace(message))
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

    /// <summary>
    /// 文字起こしRequestをQueueへ追加する
    /// </summary>
    public bool TryEnqueue(TranscriptionJobRequest request)
    {
        var diagnosticsEnabled = request.Options.TranscriptionDiagnosticsLogEnabled;
        var key = NormalizePathKey(request.AudioFilePath);
        lock (_stateGate)
        {
            if (_jobStates.ContainsKey(key))
            {
                if (diagnosticsEnabled)
                {
                    _logger.LogInformation("Transcription job enqueue skipped because the file is already pending or running. File={File}", request.AudioFilePath);
                }

                return false;
            }

            // モデル削除・再取得の保護はQueue投入直後から必要なので、Channelへ公開する前に参照数を増やす。
            // Writerが拒否した場合は同じlock内で必ず巻き戻し、保護状態だけが残らないようにする。
            _jobStates[key] = TranscriptionJobState.Pending;
            _jobRequests[key] = request;
            _modelUsageTracker.Acquire(request.EngineId, request.ModelId);
            if (!_queue.Writer.TryWrite(request))
            {
                _jobStates.TryRemove(key, out _);
                _jobRequests.TryRemove(key, out _);
                _modelUsageTracker.Release(request.EngineId, request.ModelId);
                _logger.LogWarning("Transcription job enqueue failed because the queue writer rejected the request. File={File}", request.AudioFilePath);
                return false;
            }
        }

        if (diagnosticsEnabled)
        {
            _logger.LogInformation(
                "Transcription job queued. File={File}, Trigger={Trigger}, Engine={Engine}, Model={Model}, Language={Language}, OutputFormats={OutputFormats}",
                request.AudioFilePath,
                request.Trigger,
                request.EngineId,
                request.ModelId,
                request.Options.TranscriptionLanguage,
                request.Options.TranscriptionOutputFormats);
        }

        JobStateChanged?.Invoke(this, new TranscriptionJobStateChangedEventArgs(request.AudioFilePath, TranscriptionJobState.Pending));
        return true;
    }

    /// <summary>待機中・実行中ジョブの状態一覧を取得する</summary>
    public IReadOnlyCollection<TranscriptionJobStateSnapshot> GetStateSnapshot()
    {
        return _jobStates
            .Select(kvp => new TranscriptionJobStateSnapshot(kvp.Key, kvp.Value))
            .ToArray();
    }

    /// <summary>
    /// 指定したEngine/Modelを参照する待機中または実行中ジョブが存在するか確認する
    /// </summary>
    public bool IsModelInUse(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        return _modelUsageTracker.IsProtected(engineId, modelId);
    }

    /// <summary>
    /// 指定したEngine/Modelを参照する待機中・実行中ジョブ数を取得する
    /// </summary>
    public int GetModelUsageCount(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        return _modelUsageTracker.GetUsageCount(engineId, modelId);
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
        var diagnosticsEnabled = request.Options.TranscriptionDiagnosticsLogEnabled;
        var priority = request.Trigger == TranscriptionTrigger.AutoAfterRecord
            ? request.Options.AutoTranscriptionPriority
            : request.Options.ManualTranscriptionPriority;

        if (diagnosticsEnabled)
        {
            _logger.LogInformation(
                "Transcription job started. File={File}, Trigger={Trigger}, Engine={Engine}, Model={Model}, Language={Language}, OutputFormats={OutputFormats}, Priority={Priority}",
                request.AudioFilePath,
                request.Trigger,
                request.EngineId,
                request.ModelId,
                request.Options.TranscriptionLanguage,
                request.Options.TranscriptionOutputFormats,
                priority);
        }

        try
        {
            if (priority == TranscriptionPriority.Low)
            {
                await Task.Delay(300, cancellationToken);
            }

            // モデル確認はASR Engineへ渡す直前に行う。Settingsの軽量確認とは異なり、
            // ここでは必須ファイルの存在と期待サイズを確認し、破損・途中配置をEngineへ渡さない。
            var modelRequirement = await _modelRequirementService.EnsureReadyAsync(request, cancellationToken);
            if (!modelRequirement.Ready)
            {
                stopwatch.Stop();
                return new TranscriptionJobResult(
                    Succeeded: false,
                    Message: modelRequirement.Message,
                    GeneratedFiles: Array.Empty<string>(),
                    StartedAt: DateTimeOffset.Now,
                    FinishedAt: DateTimeOffset.Now);
            }

            Interlocked.Exchange(ref _transcriptionDiagnosticsActive, diagnosticsEnabled ? 1 : 0);
            TranscriptionJobResult result;
            try
            {
                // QueueはASR実装を知らず、実行対象の選択と処理はOrchestratorへ委譲する。
                result = await _orchestrator.TranscribeAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Exchange(ref _transcriptionDiagnosticsActive, 0);
            }

            stopwatch.Stop();

            if (diagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Transcription job finished. File={File}, Succeeded={Succeeded}, ElapsedMs={ElapsedMs}, GeneratedFileCount={GeneratedFileCount}, Message={Message}",
                    request.AudioFilePath,
                    result.Succeeded,
                    stopwatch.ElapsedMilliseconds,
                    result.GeneratedFiles.Count,
                    result.Message);
            }

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
        finally
        {
            Interlocked.Exchange(ref _transcriptionDiagnosticsActive, 0);
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
        if (_jobRequests.TryRemove(key, out var request))
        {
            _modelUsageTracker.Release(request.EngineId, request.ModelId);
        }
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

        // Workerが終了前に未処理ジョブを残した場合でも、モデル保護参照だけが残らないように解放する。
        lock (_stateGate)
        {
            foreach (var request in _jobRequests.Values)
            {
                _modelUsageTracker.Release(request.EngineId, request.ModelId);
            }
            _jobRequests.Clear();
            _jobStates.Clear();
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
