using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Engine IDから対応するモデルProviderを解決する
/// </summary>
public sealed class TranscriptionModelProviderResolver
{
    private readonly IReadOnlyDictionary<TranscriptionEngineId, ITranscriptionModelProvider> _providers;

    /// <summary>
    /// 登録済みProviderからResolverを構築する
    /// </summary>
    /// <param name="providers">DIコンテナに登録されたEngine別Provider</param>
    public TranscriptionModelProviderResolver(IEnumerable<ITranscriptionModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // 同じEngineを複数Providerが管理すると配置先や更新規則が曖昧になるため、起動時に重複を検出する。
        _providers = providers.ToDictionary(provider => provider.EngineId);
    }

    /// <summary>
    /// 指定したEngineのモデルProviderを取得する
    /// </summary>
    /// <exception cref="NotSupportedException">対応するProviderが登録されていない場合</exception>
    public ITranscriptionModelProvider Resolve(TranscriptionEngineId engineId)
    {
        if (_providers.TryGetValue(engineId, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"文字起こしEngine '{engineId}' のモデルProviderが登録されていません。");
    }
}
