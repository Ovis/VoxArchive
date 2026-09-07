using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper固有settings JSONとtyped optionsの相互変換を担当する
/// </summary>
public sealed class WhisperEngineSettingsProvider : ITranscriptionEngineSettingsProvider
{
    /// <inheritdoc />
    public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion)
    {
        if (schemaVersion != 1)
        {
            throw new NotSupportedException($"未対応のWhisper settings schemaVersionです: {schemaVersion}");
        }

        var modelId = ReadString(settings, "modelId") ?? "small";
        var modeText = ReadString(settings, "executionMode") ?? "auto";
        if (!Enum.TryParse<WhisperExecutionMode>(modeText, true, out var executionMode))
        {
            throw new InvalidDataException($"未対応のWhisper executionModeです: {modeText}");
        }

        return new WhisperEngineOptions
        {
            ModelId = new TranscriptionModelId(modelId),
            ExecutionMode = executionMode,
            Language = ReadString(settings, "language") ?? string.Empty
        };
    }

    /// <inheritdoc />
    public JsonElement Serialize(ITranscriptionEngineOptions options)
    {
        var whisper = options as WhisperEngineOptions
            ?? throw new ArgumentException("Whisper以外のEngine optionsが渡されました。", nameof(options));
        return JsonSerializer.SerializeToElement(new
        {
            modelId = whisper.ModelId.Value,
            executionMode = whisper.ExecutionMode.ToString().ToLowerInvariant()
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options)
    {
        if (options is not WhisperEngineOptions whisper)
        {
            return [new("whisper.options.type", "Whisper以外のEngine optionsが渡されました。")];
        }

        var errors = new List<TranscriptionValidationError>();
        if (string.IsNullOrWhiteSpace(whisper.ModelId.Value))
        {
            errors.Add(new("whisper.model.required", "Whisperモデルを選択してください。"));
        }
        return errors;
    }

    private static string? ReadString(JsonElement settings, string name)
        => settings.ValueKind == JsonValueKind.Object
           && settings.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
