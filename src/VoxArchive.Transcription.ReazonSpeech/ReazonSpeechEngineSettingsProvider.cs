using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech固有settings JSONをtyped optionsへ変換する
/// </summary>
public sealed class ReazonSpeechEngineSettingsProvider : ITranscriptionEngineSettingsProvider
{
    /// <inheritdoc />
    public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion)
    {
        if (schemaVersion != 1)
        {
            throw new NotSupportedException($"未対応のReazonSpeech settings schemaVersionです: {schemaVersion}");
        }

        var modelId = settings.ValueKind == JsonValueKind.Object
                      && settings.TryGetProperty("modelId", out var model)
                      && model.ValueKind == JsonValueKind.String
            ? model.GetString()
            : null;

        return new ReazonSpeechEngineOptions
        {
            ModelId = new TranscriptionModelId(string.IsNullOrWhiteSpace(modelId) ? "ja" : modelId)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options)
    {
        if (options is not ReazonSpeechEngineOptions reazon)
        {
            return [new("reazonspeech.options.type", "ReazonSpeech以外のEngine optionsが渡されました。")];
        }

        return string.Equals(reazon.ModelId.Value, "ja", StringComparison.OrdinalIgnoreCase)
            ? []
            : [new("reazonspeech.model.unsupported", $"未対応のReazonSpeechモデルです: {reazon.ModelId}")];
    }
}
