using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// queued/running Jobが使用するモデルをreservationで保護する
/// </summary>
public sealed class TranscriptionModelUsageTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<TranscriptionModelKey, int> _counts = [];

    /// <summary>
    /// enqueue前にモデル使用権を予約する
    /// </summary>
    public TranscriptionModelUsageReservation Acquire(TranscriptionModelKey key)
    {
        lock (_gate)
        {
            _counts.TryGetValue(key, out var current);
            _counts[key] = current + 1;
        }
        return new TranscriptionModelUsageReservation(this, key);
    }

    /// <summary>指定モデルがqueued/running Jobから保護されているか確認する</summary>
    public bool IsInUse(TranscriptionModelKey key)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(key, out var count) && count > 0;
        }
    }

    internal void Release(TranscriptionModelKey key)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(key, out var count)) return;
            if (count <= 1) _counts.Remove(key);
            else _counts[key] = count - 1;
        }
    }
}

/// <summary>
/// Job lifecycleと同じ寿命でモデル使用保護を保持する
/// </summary>
public sealed class TranscriptionModelUsageReservation : IDisposable
{
    private readonly TranscriptionModelUsageTracker _owner;
    private readonly TranscriptionModelKey _key;
    private int _disposed;

    internal TranscriptionModelUsageReservation(TranscriptionModelUsageTracker owner, TranscriptionModelKey key)
    {
        _owner = owner;
        _key = key;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release(_key);
        }
    }
}
