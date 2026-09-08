using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Application;

/// <summary>
/// Engine別のモデル管理操作capabilityを使い、Presentation向け論理モデルIDを内部物理モデルIDへ変換する
/// </summary>
public sealed class TranscriptionModelOperationResolverService : ITranscriptionModelOperationResolverService
{
    private readonly IReadOnlyDictionary<TranscriptionEngineId, ITranscriptionModelOperationCapability> _capabilities;

    /// <summary>DI登録済みcapabilityからresolverを構築する</summary>
    public TranscriptionModelOperationResolverService(IEnumerable<ITranscriptionModelOperationCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities.ToDictionary(x => x.EngineId);
    }

    /// <inheritdoc />
    public string ResolveModelId(
        string engineId,
        string logicalModelId,
        IReadOnlyDictionary<string, string> advancedSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalModelId);
        ArgumentNullException.ThrowIfNull(advancedSettings);

        var id = new TranscriptionEngineId(engineId);
        if (!_capabilities.TryGetValue(id, out var capability))
        {
            // 通常Engineは論理モデルIDと物理モデルIDが一致するため、専用capabilityがなければそのまま利用する。
            return logicalModelId;
        }

        return capability.ResolveModel(new TranscriptionModelId(logicalModelId), advancedSettings).Value;
    }
}
