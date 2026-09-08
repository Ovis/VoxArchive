using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Application;

/// <summary>
/// Engine固有詳細設定capabilityと永続化settingsの変換を仲介する
/// </summary>
public sealed class TranscriptionEngineAdvancedSettingsService : ITranscriptionEngineAdvancedSettingsService
{
    private readonly TranscriptionEngineRegistry _registry;
    private readonly IReadOnlyDictionary<TranscriptionEngineId, ITranscriptionEngineAdvancedSettingsCapability> _capabilities;

    /// <summary>
    /// RegistryとDI登録済みadvanced-settings capabilityからServiceを構築する
    /// </summary>
    public TranscriptionEngineAdvancedSettingsService(
        TranscriptionEngineRegistry registry,
        IEnumerable<ITranscriptionEngineAdvancedSettingsCapability> capabilities)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities.ToDictionary(x => x.EngineId);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetValues(string engineId, TranscriptionEngineSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentNullException.ThrowIfNull(settings);
        var id = new TranscriptionEngineId(engineId);
        var registration = _registry.Get(id);
        var capability = GetCapability(id);
        var options = registration.SettingsProvider.Deserialize(settings.Settings, settings.SchemaVersion);
        return capability.GetValues(options);
    }

    /// <inheritdoc />
    public TranscriptionEngineSettings UpdateValues(
        string engineId,
        TranscriptionEngineSettings settings,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);
        var id = new TranscriptionEngineId(engineId);
        var registration = _registry.Get(id);
        var capability = GetCapability(id);
        var options = registration.SettingsProvider.Deserialize(settings.Settings, settings.SchemaVersion);
        var updated = capability.ApplyValues(options, values);
        var errors = registration.SettingsProvider.Validate(updated);
        if (errors.Count > 0)
        {
            // UI値を勝手に補正せず、Engine validation結果をそのまま利用者へ返せる形で失敗させる。
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(x => x.Message)));
        }

        return settings with { Settings = registration.SettingsProvider.Serialize(updated) };
    }

    private ITranscriptionEngineAdvancedSettingsCapability GetCapability(TranscriptionEngineId engineId)
        => _capabilities.TryGetValue(engineId, out var capability)
            ? capability
            : throw new InvalidOperationException($"Engine '{engineId}' は詳細設定を公開していません。");
}
