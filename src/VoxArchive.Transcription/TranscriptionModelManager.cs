using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine別Model Providerを横断してdownload、readiness、integrity、usage protectionを調停する
/// </summary>
public sealed class TranscriptionModelManager(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionModelUsageTracker usageTracker)
{
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly object _activeGate = new();
    private readonly Dictionary<TranscriptionModelKey, Task<TranscriptionModelInstallation>> _activeDownloads = [];
    private CancellationTokenSource? _activeDownloadCancellation;

    /// <summary>モデルがJob実行可能な軽量readinessを満たすか確認する</summary>
    public bool IsReady(TranscriptionModelKey key) => GetProvider(key.EngineId).IsReady(key.ModelId);

    /// <summary>readyなモデルの物理配置を取得する</summary>
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelKey key)
        => GetProvider(key.EngineId).GetInstallation(key.ModelId);

    /// <summary>
    /// モデルを取得する。同一モデルへの並行要求はownerのTaskを共有する
    /// </summary>
    public Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelKey key,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_activeGate)
        {
            if (_activeDownloads.TryGetValue(key, out var existing))
            {
                // waiter側のキャンセルでowner downloadを中止しない。待機だけをキャンセルする。
                return existing.WaitAsync(cancellationToken);
            }

            var task = InstallOwnedAsync(key, force, progress);
            _activeDownloads.Add(key, task);
            return task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 同一モデルが現在download中なら完了を待つ。activeでなければfalseを返す
    /// </summary>
    public async Task<bool> WaitForActiveDownloadAsync(
        TranscriptionModelKey key,
        CancellationToken cancellationToken = default)
    {
        Task<TranscriptionModelInstallation>? task;
        lock (_activeGate)
        {
            _activeDownloads.TryGetValue(key, out task);
        }
        if (task is null) return false;
        await task.WaitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// queued/running Jobに使われていないモデルを削除する
    /// </summary>
    public async Task DeleteAsync(TranscriptionModelKey key, CancellationToken cancellationToken = default)
    {
        EnsureNotInUse(key, "削除");
        await GetProvider(key.EngineId).DeleteAsync(key.ModelId, cancellationToken);
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

    /// <summary>
    /// 現在のdownload ownerへキャンセルを通知し、完了まで待つ
    /// </summary>
    public async Task CancelActiveDownloadAndWaitAsync()
    {
        Task[] active;
        lock (_activeGate)
        {
            _activeDownloadCancellation?.Cancel();
            active = _activeDownloads.Values.Cast<Task>().ToArray();
        }
        if (active.Length > 0)
        {
            try { await Task.WhenAll(active); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<TranscriptionModelInstallation> InstallOwnedAsync(
        TranscriptionModelKey key,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress)
    {
        // InstallAsyncは_activeGate保持中にowner Taskを生成する。
        // Semaphoreが即時獲得できても同じlockへ同期的に再入しないよう、必ず非同期境界を作る。
        await Task.Yield();
        await _downloadGate.WaitAsync();
        var cts = new CancellationTokenSource();
        lock (_activeGate)
        {
            _activeDownloadCancellation = cts;
        }

        try
        {
            EnsureNotInUse(key, force ? "再取得" : "取得");
            return await GetProvider(key.EngineId).InstallAsync(key.ModelId, force, progress, cts.Token);
        }
        finally
        {
            lock (_activeGate)
            {
                _activeDownloads.Remove(key);
                if (ReferenceEquals(_activeDownloadCancellation, cts)) _activeDownloadCancellation = null;
            }
            cts.Dispose();
            _downloadGate.Release();
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
}
