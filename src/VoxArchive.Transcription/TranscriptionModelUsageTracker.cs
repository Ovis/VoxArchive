using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// queued/running Jobが使用するモデルをreservationで保護し、モデル管理操作とのglobal排他を提供する
/// </summary>
public sealed class TranscriptionModelUsageTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<TranscriptionModelKey, int> _counts = [];
    private string? _reservationBlockReason;

    /// <summary>
    /// enqueue前にモデル使用権を予約する
    /// </summary>
    public TranscriptionModelUsageReservation Acquire(TranscriptionModelKey key)
    {
        lock (_gate)
        {
            if (_reservationBlockReason is not null)
            {
                throw new InvalidOperationException("モデルの処理中です。完了後に再度実行してください。");
            }

            _counts.TryGetValue(key, out var current);
            _counts[key] = current + 1;
        }
        return new TranscriptionModelUsageReservation(this, key);
    }

    /// <summary>
    /// 文字起こしreservationが存在しない場合だけ、新規reservationを一時的に禁止するleaseを取得する
    /// </summary>
    /// <remarks>
    /// 既存reservation確認とblock設定を同じlockで行うため、モデル操作開始とJob Admissionの競合を原子的に解決できる。
    /// </remarks>
    public TranscriptionModelUsageBlock BlockNewReservations(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_reservationBlockReason is not null)
            {
                throw new InvalidOperationException($"別のモデル処理中です: {_reservationBlockReason}");
            }
            if (_counts.Values.Any(count => count > 0))
            {
                throw new InvalidOperationException($"文字起こし処理中のためモデルを{reason}できません。");
            }

            _reservationBlockReason = reason;
            return new TranscriptionModelUsageBlock(this);
        }
    }

    /// <summary>指定モデルがqueued/running Jobから保護されているか確認する</summary>
    public bool IsInUse(TranscriptionModelKey key)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(key, out var count) && count > 0;
        }
    }

    /// <summary>いずれかの文字起こしJobがモデル利用権を保持しているか確認する</summary>
    public bool AnyInUse()
    {
        lock (_gate)
        {
            return _counts.Values.Any(count => count > 0);
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

    internal void ReleaseBlock()
    {
        lock (_gate)
        {
            _reservationBlockReason = null;
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

/// <summary>
/// モデル管理操作中に新規文字起こしreservationを禁止するleaseを保持する
/// </summary>
public sealed class TranscriptionModelUsageBlock : IDisposable
{
    private readonly TranscriptionModelUsageTracker _owner;
    private int _disposed;

    internal TranscriptionModelUsageBlock(TranscriptionModelUsageTracker owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.ReleaseBlock();
        }
    }
}
