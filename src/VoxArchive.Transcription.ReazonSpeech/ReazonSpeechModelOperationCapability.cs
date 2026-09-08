using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeechの論理モデルとprecision設定から、モデル管理で使用する物理package IDを解決する
/// </summary>
public sealed class ReazonSpeechModelOperationCapability : ITranscriptionModelOperationCapability
{
    /// <inheritdoc />
    public TranscriptionEngineId EngineId => ReazonSpeechEngineIdentity.EngineId;

    /// <inheritdoc />
    public TranscriptionModelId ResolveModel(
        TranscriptionModelId logicalModelId,
        IReadOnlyDictionary<string, string> advancedSettings)
    {
        ArgumentNullException.ThrowIfNull(advancedSettings);
        if (logicalModelId != ReazonSpeechModelCatalog.JapaneseModelId)
        {
            throw new NotSupportedException($"未対応のReazonSpeech論理モデルです: {logicalModelId}");
        }

        if (!advancedSettings.TryGetValue(ReazonSpeechAdvancedSettingsCapability.PrecisionKey, out var precision))
        {
            throw new ArgumentException(
                $"ReazonSpeech詳細設定 '{ReazonSpeechAdvancedSettingsCapability.PrecisionKey}' がありません。",
                nameof(advancedSettings));
        }

        return precision.Trim().ToLowerInvariant() switch
        {
            "fp32" => ReazonSpeechModelCatalog.JapaneseFp32PackageId,
            "int8" => ReazonSpeechModelCatalog.JapaneseInt8PackageId,
            "int8-fp32" => ReazonSpeechModelCatalog.JapaneseInt8Fp32PackageId,
            _ => throw new ArgumentException($"未対応のReazonSpeech precisionです: {precision}", nameof(advancedSettings))
        };
    }
}
