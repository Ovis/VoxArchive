using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engineとoptional capabilityをEngine ID単位で管理する
/// </summary>
public sealed class TranscriptionEngineRegistry
{
    private readonly IReadOnlyDictionary<TranscriptionEngineId, TranscriptionEngineRegistration> _registrations;

    /// <summary>
    /// 登録一覧を検証してRegistryを構築する
    /// </summary>
    /// <param name="registrations">Runtime Composition Rootが収集したEngine registration</param>
    public TranscriptionEngineRegistry(IEnumerable<TranscriptionEngineRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var dictionary = new Dictionary<TranscriptionEngineId, TranscriptionEngineRegistration>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!dictionary.TryAdd(registration.Engine.Id, registration))
            {
                throw new InvalidOperationException($"文字起こしEngine IDが重複しています: {registration.Engine.Id}");
            }

            if (registration.ModelProvider is not null
                && registration.ModelProvider.EngineId != registration.Engine.Id)
            {
                throw new InvalidOperationException(
                    $"Model ProviderのEngine IDがRegistrationと一致しません。Engine={registration.Engine.Id}, Provider={registration.ModelProvider.EngineId}");
            }
        }

        _registrations = dictionary;
    }

    /// <summary>
    /// 指定Engine IDのregistrationを取得する
    /// </summary>
    public TranscriptionEngineRegistration Get(TranscriptionEngineId engineId)
    {
        return _registrations.TryGetValue(engineId, out var registration)
            ? registration
            : throw new KeyNotFoundException($"未登録の文字起こしEngineです: {engineId}");
    }

    /// <summary>
    /// 登録済みEngine一覧を取得する
    /// </summary>
    public IReadOnlyCollection<TranscriptionEngineRegistration> GetAll() => _registrations.Values.ToArray();
}
