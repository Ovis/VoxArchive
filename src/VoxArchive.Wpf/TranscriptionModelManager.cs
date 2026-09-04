using Microsoft.Extensions.Logging;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// Engineに依存しない文字起こしモデル管理をアプリケーション全体で調停する
/// </summary>
/// <remarks>
/// 設定Windowより長く生存するSingletonとして、モデル状態確認・取得の単一実行制約・進捗共有・
/// Queue投入済みモデルの保護を一元管理する。UIはこのサービスの状態を観測するだけで取得処理を所有しない。
/// </remarks>
public sealed class TranscriptionModelManager
{
    private readonly Lock _gate = new();
    private readonly TranscriptionModelProviderResolver _providerResolver;
    private readonly TranscriptionJobQueue _transcriptionQueue;
    private readonly ILogger<TranscriptionModelManager> _logger;
    private ActiveDownload? _activeDownload;

    /// <summary>モデル管理サービスを初期化する</summary>
    public TranscriptionModelManager(
        TranscriptionModelProviderResolver providerResolver,
        TranscriptionJobQueue transcriptionQueue,
        ILogger<TranscriptionModelManager> logger)
    {
        _providerResolver = providerResolver;
        _transcriptionQueue = transcriptionQueue;
        _logger = logger;
    }

    /// <summary>取得進捗や取得開始・終了が変化したときに通知する</summary>
    public event EventHandler? StateChanged;

    /// <summary>モデル取得が正常終了したときに通知する</summary>
    public event EventHandler<TranscriptionModelDownloadCompletedEventArgs>? DownloadCompleted;

    /// <summary>モデル取得が失敗したときに通知する</summary>
    public event EventHandler<TranscriptionModelDownloadFailedEventArgs>? DownloadFailed;

    /// <summary>選択可能なモデル一覧を取得する</summary>
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels(TranscriptionEngineId engineId)
    {
        return _providerResolver.Resolve(engineId).GetAvailableModels();
    }

    /// <summary>指定した検証レベルでモデル状態を確認する</summary>
    public TranscriptionModelInspection Inspect(
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        TranscriptionModelInspectionLevel level)
    {
        if (level == TranscriptionModelInspectionLevel.Hash && IsModelProtected(engineId, modelId))
        {
            throw new InvalidOperationException("文字起こしで使用中のモデルは完全性確認できません。ジョブ完了後に再確認してください。");
        }

        return _providerResolver.Resolve(engineId).Inspect(modelId, level);
    }

    /// <summary>SHA-256を含む完全性確認をUIスレッドを占有せず実行する</summary>
    public Task<TranscriptionModelInspection> VerifyAsync(
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        CancellationToken cancellationToken = default)
    {
        if (IsModelProtected(engineId, modelId))
        {
            throw new InvalidOperationException("文字起こしで使用中のモデルは完全性確認できません。ジョブ完了後に再確認してください。");
        }

        return Task.Run(
            () => _providerResolver.Resolve(engineId).Inspect(modelId, TranscriptionModelInspectionLevel.Hash),
            cancellationToken);
    }

    /// <summary>文字起こし実行前に必要な存在・サイズ条件を満たすか確認する</summary>
    public bool IsReadyForExecution(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        return Inspect(engineId, modelId, TranscriptionModelInspectionLevel.Size).State == TranscriptionModelPackageState.Installed;
    }

    /// <summary>指定したモデルが待機中または実行中の文字起こしJobに参照されているか確認する</summary>
    public bool IsModelProtected(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        return _transcriptionQueue.IsModelInUse(engineId, modelId);
    }

    /// <summary>現在実行中のモデル取得状態を取得する</summary>
    public TranscriptionModelDownloadSnapshot? GetActiveDownload()
    {
        lock (_gate)
        {
            return _activeDownload?.ToSnapshot();
        }
    }

    /// <summary>
    /// モデル取得へ参加する。同じモデルの取得中なら既存処理を共有し、別モデル取得中なら拒否する
    /// </summary>
    public TranscriptionModelDownloadParticipation AcquireDownload(
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        bool force)
    {
        TranscriptionModelDownloadParticipation participation;
        lock (_gate)
        {
            if (_activeDownload is not null)
            {
                if (_activeDownload.EngineId != engineId || _activeDownload.ModelId != modelId)
                {
                    throw new TranscriptionModelDownloadBusyException(_activeDownload.ToSnapshot());
                }

                _activeDownload.WaiterCount++;
                participation = new TranscriptionModelDownloadParticipation(
                    this,
                    _activeDownload.Id,
                    _activeDownload.Completion,
                    startedDownload: false);
            }
            else
            {
                if (IsModelProtected(engineId, modelId))
                {
                    throw new InvalidOperationException("文字起こしで使用中のモデルは取得・再取得できません。");
                }

                var provider = _providerResolver.Resolve(engineId);
                var descriptor = provider.GetAvailableModels().FirstOrDefault(item => item.ModelId == modelId)
                    ?? throw new NotSupportedException($"モデル '{engineId}/{modelId}' はサポートされていません。");
                var active = new ActiveDownload(
                    Guid.NewGuid(),
                    engineId,
                    modelId,
                    descriptor.DisplayName,
                    force,
                    new CancellationTokenSource());
                _activeDownload = active;
                active.Completion = RunDownloadAsync(active, provider);
                participation = new TranscriptionModelDownloadParticipation(this, active.Id, active.Completion, startedDownload: true);
            }
        }

        RaiseStateChanged();
        return participation;
    }

    /// <summary>
    /// 現在のモデル取得そのものをキャンセルする
    /// </summary>
    /// <remarks>
    /// Owner側UIはWaiterCountが1以上の場合に影響確認を行ってから呼び出す。
    /// </remarks>
    public bool CancelActiveDownload(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        var canceled = false;
        lock (_gate)
        {
            if (_activeDownload is null || _activeDownload.EngineId != engineId || _activeDownload.ModelId != modelId)
            {
                return false;
            }

            if (!_activeDownload.Cancellation.IsCancellationRequested)
            {
                _activeDownload.Cancellation.Cancel();
                _activeDownload.IsCancelling = true;
                canceled = true;
            }
        }

        if (canceled)
        {
            RaiseStateChanged();
        }

        return true;
    }

    /// <summary>指定モデルを削除する</summary>
    public async Task DeleteAsync(
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeDownload is not null && _activeDownload.EngineId == engineId && _activeDownload.ModelId == modelId)
            {
                throw new InvalidOperationException("取得中のモデルは削除できません。");
            }
        }

        if (IsModelProtected(engineId, modelId))
        {
            throw new InvalidOperationException("文字起こしで使用中のモデルは削除できません。");
        }

        await _providerResolver.Resolve(engineId).DeleteAsync(modelId, cancellationToken);
        RaiseStateChanged();
    }

    private async Task<TranscriptionModelInstallation> RunDownloadAsync(
        ActiveDownload active,
        ITranscriptionModelProvider provider)
    {
        var progress = new Progress<TranscriptionModelTransferProgress>(value =>
        {
            lock (_gate)
            {
                if (_activeDownload?.Id != active.Id)
                {
                    return;
                }

                active.BytesReceived = value.BytesReceived;
                active.TotalBytes = value.TotalBytes;
            }

            RaiseStateChanged();
        });

        try
        {
            var installation = await provider.InstallManagedAsync(
                active.ModelId,
                active.Force,
                progress,
                active.Cancellation.Token);
            _logger.LogInformation(
                "Transcription model download completed. Engine={Engine}, Model={Model}",
                active.EngineId,
                active.ModelId);
            DownloadCompleted?.Invoke(this, new TranscriptionModelDownloadCompletedEventArgs(active.EngineId, active.ModelId, active.ModelDisplayName));
            return installation;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Transcription model download canceled. Engine={Engine}, Model={Model}",
                active.EngineId,
                active.ModelId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Transcription model download failed. Engine={Engine}, Model={Model}",
                active.EngineId,
                active.ModelId);
            DownloadFailed?.Invoke(this, new TranscriptionModelDownloadFailedEventArgs(active.EngineId, active.ModelId, active.ModelDisplayName, ex));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (_activeDownload?.Id == active.Id)
                {
                    _activeDownload = null;
                }
            }

            active.Cancellation.Dispose();
            RaiseStateChanged();
        }
    }

    /// <summary>共有取得を待機していた参加者の登録を解除する</summary>
    internal void ReleaseWaiter(Guid operationId)
    {
        var changed = false;
        lock (_gate)
        {
            if (_activeDownload?.Id == operationId && _activeDownload.WaiterCount > 0)
            {
                _activeDownload.WaiterCount--;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ActiveDownload(
        Guid id,
        TranscriptionEngineId engineId,
        TranscriptionModelId modelId,
        string modelDisplayName,
        bool force,
        CancellationTokenSource cancellation)
    {
        public Guid Id { get; } = id;
        public TranscriptionEngineId EngineId { get; } = engineId;
        public TranscriptionModelId ModelId { get; } = modelId;
        public string ModelDisplayName { get; } = modelDisplayName;
        public bool Force { get; } = force;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<TranscriptionModelInstallation> Completion { get; set; } = null!;
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int WaiterCount { get; set; }
        public bool IsCancelling { get; set; }

        public TranscriptionModelDownloadSnapshot ToSnapshot()
        {
            return new TranscriptionModelDownloadSnapshot(
                EngineId,
                ModelId,
                ModelDisplayName,
                BytesReceived,
                TotalBytes,
                WaiterCount,
                IsCancelling);
        }
    }
}

/// <summary>進行中のモデル取得状態を表す</summary>
public sealed record TranscriptionModelDownloadSnapshot(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    string ModelDisplayName,
    long BytesReceived,
    long TotalBytes,
    int WaiterCount,
    bool IsCancelling)
{
    /// <summary>0～100の進捗率を取得する</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>共有モデル取得への参加Handleを表す</summary>
public sealed class TranscriptionModelDownloadParticipation : IDisposable
{
    private readonly TranscriptionModelManager _manager;
    private readonly Guid _operationId;
    private int _disposed;

    internal TranscriptionModelDownloadParticipation(
        TranscriptionModelManager manager,
        Guid operationId,
        Task<TranscriptionModelInstallation> completion,
        bool startedDownload)
    {
        _manager = manager;
        _operationId = operationId;
        Completion = completion;
        StartedDownload = startedDownload;
    }

    /// <summary>この参加者が新しい取得処理を開始したか</summary>
    public bool StartedDownload { get; }

    /// <summary>共有しているモデル取得の完了Task</summary>
    public Task<TranscriptionModelInstallation> Completion { get; }

    /// <summary>待機を終了し、共有取得のWaiter登録を解除する</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || StartedDownload)
        {
            return;
        }

        _manager.ReleaseWaiter(_operationId);
    }
}

/// <summary>モデル取得完了通知を表す</summary>
public sealed record TranscriptionModelDownloadCompletedEventArgs(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    string ModelDisplayName);

/// <summary>モデル取得失敗通知を表す</summary>
public sealed record TranscriptionModelDownloadFailedEventArgs(
    TranscriptionEngineId EngineId,
    TranscriptionModelId ModelId,
    string ModelDisplayName,
    Exception Exception);

/// <summary>
/// 別モデルの取得がアプリケーション全体で既に進行中の場合の例外を表す
/// </summary>
public sealed class TranscriptionModelDownloadBusyException(TranscriptionModelDownloadSnapshot activeDownload)
    : InvalidOperationException($"現在 {activeDownload.EngineId.Value} / {activeDownload.ModelDisplayName} のモデルを取得中です。")
{
    /// <summary>競合している取得状態</summary>
    public TranscriptionModelDownloadSnapshot ActiveDownload { get; } = activeDownload;
}
