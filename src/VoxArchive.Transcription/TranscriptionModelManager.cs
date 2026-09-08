using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine別Model Providerを横断してdownload、readiness、再確認、削除、usage protectionを調停する
/// </summary>
/// <remarks>
/// モデル管理操作はアプリケーション全体で1件に制限し、queued/running文字起こしとは同時実行しない。
/// 同一モデルdownloadへの並行要求だけはowner Taskを共有する。
/// </remarks>
public sealed class TranscriptionModelManager(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionModelUsageTracker usageTracker)
{
    private readonly object _gate = new();
    private ActiveDownload? _activeDownload;
    private string? _activeExclusiveOperation;
    private TranscriptionModelUsageBlock? _exclusiveUsageBlock;

    /// <summary>取得開始・進捗・終了・キャンセル状態が変化したときに通知する</summary>
    public event EventHandler? StateChanged;

    /// <summary>指定Engineで選択可能なモデル一覧を取得する</summary>
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels(TranscriptionEngineId engineId)
        => GetProvider(engineId).GetAvailableModels();

    /// <summary>モデルがJob実行可能なreadinessを満たすか確認する</summary>
    public bool IsReady(TranscriptionModelKey key) => GetProvider(key.EngineId).IsReady(key.ModelId);

    /// <summary>指定レベルでモデル配置状態を確認する</summary>
    public TranscriptionModelInspection Inspect(TranscriptionModelKey key, TranscriptionModelInspectionLevel level)
    {
        if (level != TranscriptionModelInspectionLevel.Hash)
        {
            return GetProvider(key.EngineId).Inspect(key.ModelId, level);
        }

        BeginExclusiveOperation("再確認");
        try
        {
            return GetProvider(key.EngineId).Inspect(key.ModelId, level);
        }
        finally
        {
            EndExclusiveOperation();
        }
    }

    /// <summary>readyなモデルの物理配置を取得する</summary>
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelKey key)
        => GetProvider(key.EngineId).GetInstallation(key.ModelId);

    /// <summary>queued/running Jobが指定モデルを保護しているか確認する</summary>
    public bool IsInUse(TranscriptionModelKey key) => usageTracker.IsInUse(key);

    /// <summary>download/delete/recheckのいずれかが進行中か確認する</summary>
    public bool IsManagementOperationInProgress()
    {
        lock (_gate)
        {
            return _activeDownload is not null || _activeExclusiveOperation is not null;
        }
    }

    /// <summary>
    /// モデル管理操作と競合しない状態で文字起こし用reservationを取得する
    /// </summary>
    public TranscriptionModelUsageReservation ReserveForTranscription(TranscriptionModelKey key)
        => usageTracker.Acquire(key);

    /// <summary>現在進行中のモデル取得snapshotを取得する</summary>
    public TranscriptionModelDownloadSnapshot? GetActiveDownload()
    {
        lock (_gate)
        {
            return _activeDownload?.ToSnapshot();
        }
    }

    /// <summary>
    /// モデルを取得する。同一モデルへの並行要求は既存ownerを共有し、その他のモデル管理操作中は拒否する
    /// </summary>
    public Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelKey key,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ActiveDownload active;
        var isWaiter = false;
        lock (_gate)
        {
            if (_activeExclusiveOperation is not null)
            {
                throw new InvalidOperationException($"別のモデル処理中です: {_activeExclusiveOperation}");
            }

            if (_activeDownload is not null)
            {
                if (_activeDownload.Key != key)
                {
                    throw new TranscriptionModelDownloadBusyException(_activeDownload.ToSnapshot());
                }

                _activeDownload.WaiterCount++;
                active = _activeDownload;
                isWaiter = true;
            }
            else
            {
                // 既存reservation確認と新規reservation禁止をUsageTrackerの同じlockで行う。
                // AdmissionがManager経由でなく直接Acquireしても、このlease保持中は確実に拒否される。
                var usageBlock = usageTracker.BlockNewReservations(force ? "再取得" : "取得");
                try
                {
                    var provider = GetProvider(key.EngineId);
                    var descriptor = ResolveDownloadDescriptor(provider, key.ModelId)
                        ?? throw new NotSupportedException($"未対応のモデルです: {key.EngineId}/{key.ModelId}");
                    active = new ActiveDownload(
                        key,
                        descriptor.DisplayName,
                        force,
                        new CancellationTokenSource(),
                        progress,
                        usageBlock);
                    _activeDownload = active;
                    active.Completion = RunOwnedDownloadAsync(active);
                }
                catch
                {
                    usageBlock.Dispose();
                    throw;
                }
            }
        }

        RaiseStateChanged();
        return isWaiter
            ? WaitAsParticipantAsync(active, cancellationToken)
            : active.Completion.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 同一モデルが現在download中なら完了を待つ。activeでなければfalseを返す
    /// </summary>
    public async Task<bool> WaitForActiveDownloadAsync(
        TranscriptionModelKey key,
        CancellationToken cancellationToken = default)
    {
        ActiveDownload? active;
        lock (_gate)
        {
            if (_activeDownload is null || _activeDownload.Key != key)
            {
                return false;
            }
            _activeDownload.WaiterCount++;
            active = _activeDownload;
        }
        RaiseStateChanged();
        await WaitAsParticipantAsync(active, cancellationToken);
        return true;
    }

    /// <summary>現在進行中の指定モデル取得をキャンセルする</summary>
    public bool CancelActiveDownload(TranscriptionModelKey key)
    {
        var changed = false;
        lock (_gate)
        {
            if (_activeDownload is null || _activeDownload.Key != key)
            {
                return false;
            }
            if (!_activeDownload.Cancellation.IsCancellationRequested)
            {
                _activeDownload.Cancellation.Cancel();
                _activeDownload.IsCancelling = true;
                changed = true;
            }
        }
        if (changed) RaiseStateChanged();
        return true;
    }

    /// <summary>文字起こしが実行中でなく、他のモデル操作もない場合だけモデルを削除する</summary>
    public async Task DeleteAsync(TranscriptionModelKey key, CancellationToken cancellationToken = default)
    {
        BeginExclusiveOperation("削除");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GetProvider(key.EngineId).DeleteAsync(key.ModelId, cancellationToken);
        }
        finally
        {
            EndExclusiveOperation();
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// 文字起こしが実行中でなく、他のモデル操作もない場合だけモデルを明示的に再確認する
    /// </summary>
    public TranscriptionModelInspection Reverify(TranscriptionModelKey key)
    {
        BeginExclusiveOperation("再確認");
        try
        {
            return GetProvider(key.EngineId).Inspect(key.ModelId, TranscriptionModelInspectionLevel.Hash);
        }
        finally
        {
            EndExclusiveOperation();
            RaiseStateChanged();
        }
    }

    /// <summary>現在のdownload ownerへキャンセルを通知し、完了まで待つ</summary>
    public async Task CancelActiveDownloadAndWaitAsync()
    {
        Task<TranscriptionModelInstallation>? task;
        lock (_gate)
        {
            if (_activeDownload is null) return;
            if (!_activeDownload.Cancellation.IsCancellationRequested)
            {
                _activeDownload.Cancellation.Cancel();
                _activeDownload.IsCancelling = true;
            }
            task = _activeDownload.Completion;
        }
        RaiseStateChanged();

        try
        {
            await task;
        }
        catch
        {
            // 終了処理ではowner Taskの成功結果を使わず、finallyによるactive state解放だけを待つ。
        }
    }

    private async Task<TranscriptionModelInstallation> RunOwnedDownloadAsync(ActiveDownload active)
    {
        var internalProgress = new Progress<TranscriptionModelTransferProgress>(value =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_activeDownload, active)) return;
                active.BytesReceived = value.BytesReceived;
                active.TotalBytes = value.TotalBytes;
                active.CurrentFileName = value.CurrentFileName;
                active.IsValidating = value.IsValidating;
            }
            active.ExternalProgress?.Report(value);
            RaiseStateChanged();
        });

        try
        {
            return await GetProvider(active.Key.EngineId).InstallAsync(
                active.Key.ModelId,
                active.Force,
                internalProgress,
                active.Cancellation.Token);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeDownload, active)) _activeDownload = null;
            }
            active.UsageBlock.Dispose();
            active.Cancellation.Dispose();
            RaiseStateChanged();
        }
    }

    private async Task<TranscriptionModelInstallation> WaitAsParticipantAsync(
        ActiveDownload active,
        CancellationToken cancellationToken)
    {
        try
        {
            return await active.Completion.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeDownload, active) && active.WaiterCount > 0)
                {
                    active.WaiterCount--;
                }
            }
            RaiseStateChanged();
        }
    }

    private void BeginExclusiveOperation(string operation)
    {
        lock (_gate)
        {
            if (_activeDownload is not null)
            {
                throw new InvalidOperationException("モデル取得中のため別のモデル処理を開始できません。");
            }
            if (_activeExclusiveOperation is not null)
            {
                throw new InvalidOperationException($"別のモデル処理中です: {_activeExclusiveOperation}");
            }

            var usageBlock = usageTracker.BlockNewReservations(operation);
            _activeExclusiveOperation = operation;
            _exclusiveUsageBlock = usageBlock;
        }
        RaiseStateChanged();
    }

    private void EndExclusiveOperation()
    {
        TranscriptionModelUsageBlock? usageBlock;
        lock (_gate)
        {
            _activeExclusiveOperation = null;
            usageBlock = _exclusiveUsageBlock;
            _exclusiveUsageBlock = null;
        }
        usageBlock?.Dispose();
    }

    private ITranscriptionModelProvider GetProvider(TranscriptionEngineId engineId)
        => engineRegistry.Get(engineId).ModelProvider
           ?? throw new InvalidOperationException($"このEngineはmanaged modelを使用しません: {engineId}");

    private static TranscriptionModelDescriptor? ResolveDownloadDescriptor(
        ITranscriptionModelProvider provider,
        TranscriptionModelId modelId)
    {
        // 利用者向けcatalogへ物理packageを露出させないEngineだけ、内部descriptor capabilityで解決する。
        // 通常のEngineは従来どおり公開catalogをそのまま利用する。
        var internalDescriptor = (provider as ITranscriptionInternalModelDescriptorCapability)?
            .ResolveInternalDescriptor(modelId);
        return internalDescriptor
               ?? provider.GetAvailableModels().FirstOrDefault(x => x.ModelId == modelId);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private sealed class ActiveDownload(
        TranscriptionModelKey key,
        string modelDisplayName,
        bool force,
        CancellationTokenSource cancellation,
        IProgress<TranscriptionModelTransferProgress>? externalProgress,
        TranscriptionModelUsageBlock usageBlock)
    {
        public TranscriptionModelKey Key { get; } = key;
        public string ModelDisplayName { get; } = modelDisplayName;
        public bool Force { get; } = force;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IProgress<TranscriptionModelTransferProgress>? ExternalProgress { get; } = externalProgress;
        public TranscriptionModelUsageBlock UsageBlock { get; } = usageBlock;
        public Task<TranscriptionModelInstallation> Completion { get; set; } = null!;
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public string? CurrentFileName { get; set; }
        public bool IsValidating { get; set; }
        public int WaiterCount { get; set; }
        public bool IsCancelling { get; set; }

        public TranscriptionModelDownloadSnapshot ToSnapshot()
            => new(
                Key,
                ModelDisplayName,
                BytesReceived,
                TotalBytes,
                WaiterCount,
                IsCancelling,
                CurrentFileName,
                IsValidating);
    }
}

/// <summary>進行中のモデル取得状態を表す</summary>
public sealed record TranscriptionModelDownloadSnapshot(
    TranscriptionModelKey Key,
    string ModelDisplayName,
    long BytesReceived,
    long TotalBytes,
    int WaiterCount,
    bool IsCancelling,
    string? CurrentFileName = null,
    bool IsValidating = false)
{
    /// <summary>総容量不明時にtrueを返す</summary>
    public bool IsIndeterminate => TotalBytes <= 0;

    /// <summary>0～100の進捗率を取得する</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>別モデル取得が進行中で新しいdownloadを開始できないことを表す</summary>
public sealed class TranscriptionModelDownloadBusyException(TranscriptionModelDownloadSnapshot activeDownload)
    : InvalidOperationException($"別の文字起こしモデルを取得中です: {activeDownload.Key.EngineId}/{activeDownload.Key.ModelId}")
{
    public TranscriptionModelDownloadSnapshot ActiveDownload { get; } = activeDownload;
}
