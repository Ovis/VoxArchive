using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 待機中・実行中の文字起こしジョブが参照するモデルをアプリケーション全体で追跡する
/// </summary>
/// <remarks>
/// モデル管理サービスがQueue本体へ依存すると、実行前のモデル保証処理からModelManagerを利用した際に
/// 循環依存になるため、モデル保護に必要な参照数だけを独立した責務として保持する。
/// </remarks>
public sealed class TranscriptionModelUsageTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ModelKey, int> _usageCounts = [];

    /// <summary>指定したEngine/Modelへの参照を1件追加する</summary>
    public void Acquire(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        lock (_gate)
        {
            var key = new ModelKey(engineId, modelId);
            _usageCounts.TryGetValue(key, out var count);
            _usageCounts[key] = count + 1;
        }
    }

    /// <summary>指定したEngine/Modelへの参照を1件解除する</summary>
    public void Release(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        lock (_gate)
        {
            var key = new ModelKey(engineId, modelId);
            if (!_usageCounts.TryGetValue(key, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _usageCounts.Remove(key);
                return;
            }

            _usageCounts[key] = count - 1;
        }
    }

    /// <summary>指定したEngine/Modelを参照するジョブ数を取得する</summary>
    public int GetUsageCount(TranscriptionEngineId engineId, TranscriptionModelId modelId)
    {
        lock (_gate)
        {
            return _usageCounts.TryGetValue(new ModelKey(engineId, modelId), out var count) ? count : 0;
        }
    }

    /// <summary>指定したEngine/Modelが待機中または実行中ジョブから参照されているか確認する</summary>
    public bool IsProtected(TranscriptionEngineId engineId, TranscriptionModelId modelId)
        => GetUsageCount(engineId, modelId) > 0;

    private readonly record struct ModelKey(TranscriptionEngineId EngineId, TranscriptionModelId ModelId);
}
