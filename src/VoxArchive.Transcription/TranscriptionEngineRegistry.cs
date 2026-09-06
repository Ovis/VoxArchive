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

            if ((registration.ModelProvider is null) != (registration.ModelRequirementResolver is null))
            {
                // managed modelの物理操作とoptionsへの束縛は対で必要になる。
                // 片方だけを許すとAdmissionがEngine固有型を推測する必要が生じるため登録時に拒否する。
                throw new InvalidOperationException(
                    $"managed model capabilityはProviderとRequirementResolverを両方登録する必要があります: {registration.Engine.Id}");
            }
        }

        _registrations = dictionary;
    }

    /// <summary>指定Engine IDのregistrationを取得する</summary>
    public TranscriptionEngineRegistration Get(TranscriptionEngineId engineId)
        => _registrations.TryGetValue(engineId, out var registration)
            ? registration
            : throw new KeyNotFoundException($"未登録の文字起こしEngineです: {engineId}");

    /// <summary>登録済みEngine一覧を取得する</summary>
    public IReadOnlyCollection<TranscriptionEngineRegistration> GetAll() => _registrations.Values.ToArray();
}
