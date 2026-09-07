using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine別Model Providerを横断してdownload、readiness、integrity、usage protectionを調停する
/// </summary>
/// <remarks>
/// モデル取得はアプリケーション全体で1件に制限し、同一モデルへの並行要求だけowner Taskを共有する。
/// 設定UIは本クラスのsnapshotを観測し、物理Providerやdownload lifecycleを直接所有しない。
/// </remarks>
public sealed class TranscriptionModelManager(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionModelUsageTracker usageTracker)
{
    private readonly object _gate = new();
    private ActiveDownload? _activeDownload;

    /// <summary>取得開始・進捗・終了・キャンセル状態が変化したときに通知する</summary>
    public event EventHandler? StateChanged;

    /// <summary>指定Engineで選択可能なモデル一覧を取得する</summary>
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels(TranscriptionEngineId engineId)
        => GetProvider(engineId).GetAvailableModels();

    /// <summary>モデルがJob実行可能な軽量readinessを満たすか確認する</summary>
    public bool IsReady(TranscriptionModelKey key) => GetProvider(key.EngineId).IsReady(key.ModelId);

    /// <summary>指定レベルでモデル配置状態を確認する</summary>
    public TranscriptionModelInspection Inspect(TranscriptionModelKey key, TranscriptionModelInspectionLevel level)
    {
        if (level == TranscriptionModelInspectionLevel.Hash)
        {
            EnsureNotInUse(key, "SHA-256再確認");
        }
        return GetProvider(key.EngineId).Inspect(key.ModelId, level);
    }

    /// <summary>readyなモデルの物理配置を取得する</summary>
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelKey key)
        => GetProvider(key.EngineId).GetInstallation(key.ModelId);

    /// <summary>queued/running Jobが指定モデルを保護しているか確認する</summary>
    public bool IsInUse(TranscriptionModelKey key) => usageTracker.IsInUse(key);

    /// <summary>現在進行中のモデル取得snapshotを取得する</summary>
    public TranscriptionModelDownloadSnapshot? GetActiveDownload()
    {
        lock (_gate)
        {
            return _activeDownload?.ToSnapshot();
        }
    }

    /// <summary>
    /// モデルを取得する。同一モデルへの並行要求は既存ownerを共有し、別モデル取得中は拒否する
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
                EnsureNotInUse(key, force ? "再取得" : "取得");
                var descriptor = GetProvider(key.EngineId).GetAvailableModels()
                    .FirstOrDefault(x => x.ModelId == key.ModelId)
                    ?? throw new NotSupportedException($"未対応のモデルです: {key.EngineId}/{key.ModelId}");
                active = new ActiveDownload(
                    key,
                    descriptor.DisplayName,
                    force,
                    new CancellationTokenSource(),
                    progress);
                _activeDownload = active;
                active.Completion = RunOwnedDownloadAsync(active);
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

    /// <summary>queued/running Jobに使われていないモデルを削除する</summary>
    public async Task DeleteAsync(TranscriptionModelKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeDownload?.Key == key)
            {
                throw new InvalidOperationException("取得中のモデルは削除できません。");
            }
        }
        EnsureNotInUse(key, "削除");
        await GetProvider(key.EngineId).DeleteAsync(key.ModelId, cancellationToken);
        RaiseStateChanged();
    }

    /// <summary>
    /// queued/running Jobに使われていないモデルのSHA-256完全性を明示的に再確認する
    /// </summary>
    public TranscriptionModelInspection Reverify(TranscriptionModelKey key)
    {
        EnsureNotInUse(key, "SHA-256再確認");
        // SHA計算は高コストなのでJobごとには行わず、download完了時とこの明示操作だけに限定する。
        return GetProvider(key.EngineId).Inspect(key.ModelId, TranscriptionModelInspectionLevel.Hash);
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
            // アプリ終了処理ではowner Taskの成功結果を利用しない。
            // 既に通信失敗や検証失敗でfaultしていた場合も終了処理自体へ例外を伝播させず、
            // RunOwnedDownloadAsyncのfinallyによるstaging/active stateの解放完了だけを待つ。
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

    private ITranscriptionModelProvider GetProvider(TranscriptionEngineId engineId)
        => engineRegistry.Get(engineId).ModelProvider
           ?? throw new InvalidOperationException($"このEngineはmanaged modelを使用しません: {engineId}");

    private void EnsureNotInUse(TranscriptionModelKey key, string operation)
    {
        if (usageTracker.IsInUse(key))
        {
            throw new InvalidOperationException($"queued/running Jobが使用中のモデルは{operation}できません: {key.EngineId}/{key.ModelId}");
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private sealed class ActiveDownload(
        TranscriptionModelKey key,
        string modelDisplayName,
        bool force,
        CancellationTokenSource cancellation,
        IProgress<TranscriptionModelTransferProgress>? externalProgress)
    {
        public TranscriptionModelKey Key { get; } = key;
        public string ModelDisplayName { get; } = modelDisplayName;
        public bool Force { get; } = force;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IProgress<TranscriptionModelTransferProgress>? ExternalProgress { get; } = externalProgress;
        public Task<TranscriptionModelInstallation> Completion { get; set; } = null!;
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int WaiterCount { get; set; }
        public bool IsCancelling { get; set; }

        public TranscriptionModelDownloadSnapshot ToSnapshot()
            => new(
                Key,
                ModelDisplayName,
                BytesReceived,
                TotalBytes,
                WaiterCount,
                IsCancelling);
    }
}

/// <summary>進行中のモデル取得状態を表す</summary>
public sealed record TranscriptionModelDownloadSnapshot(
    TranscriptionModelKey Key,
    string ModelDisplayName,
    long BytesReceived,
    long TotalBytes,
    int WaiterCount,
    bool IsCancelling)
{
    /// <summary>0～100の進捗率を取得する</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>別モデル取得が進行中で新しいdownloadを開始できないことを表す</summary>
public sealed class TranscriptionModelDownloadBusyException(TranscriptionModelDownloadSnapshot activeDownload)
    : InvalidOperationException($"別の文字起こしモデルを取得中です: {activeDownload.Key.EngineId}/{activeDownload.Key.ModelId}")
{
    public TranscriptionModelDownloadSnapshot ActiveDownload { get; } = activeDownload;
}
