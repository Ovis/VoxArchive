using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech固有settings JSONとtyped optionsの相互変換を担当する
/// </summary>
public sealed class ReazonSpeechEngineSettingsProvider : ITranscriptionEngineSettingsProvider
{
    private const int DefaultMaxActivePaths = 4;

    /// <inheritdoc />
    public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion)
    {
        if (schemaVersion != 1)
        {
            throw new NotSupportedException($"未対応のReazonSpeech settings schemaVersionです: {schemaVersion}");
        }

        var modelId = ReadString(settings, "modelId");
        var precision = ParsePrecision(ReadString(settings, "precision"));
        var decodingMethod = ParseDecodingMethod(ReadString(settings, "decodingMethod"));
        var maxActivePaths = ReadInt32(settings, "maxActivePaths") ?? DefaultMaxActivePaths;
        var cpuThreads = ReadInt32(settings, "cpuThreads") ?? Math.Min(4, Environment.ProcessorCount);

        // 既存settingsにはmodelIdしか存在しないため、追加項目がなければ今回定義した標準値へ補完する。
        // 保存済みの旧設定をschema migrationなしで継続利用しつつ、以後の保存では値を明示する。
        return new ReazonSpeechEngineOptions
        {
            ModelId = new TranscriptionModelId(string.IsNullOrWhiteSpace(modelId) ? "ja" : modelId),
            Precision = precision,
            DecodingMethod = decodingMethod,
            MaxActivePaths = maxActivePaths,
            CpuThreads = cpuThreads
        };
    }

    /// <inheritdoc />
    public JsonElement Serialize(ITranscriptionEngineOptions options)
    {
        var reazon = options as ReazonSpeechEngineOptions
            ?? throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));
        return JsonSerializer.SerializeToElement(new
        {
            modelId = reazon.ModelId.Value,
            precision = SerializePrecision(reazon.Precision),
            decodingMethod = SerializeDecodingMethod(reazon.DecodingMethod),
            maxActivePaths = reazon.MaxActivePaths,
            cpuThreads = reazon.CpuThreads
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options)
    {
        if (options is not ReazonSpeechEngineOptions reazon)
        {
            return [new("reazonspeech.options.type", "ReazonSpeech以外のEngine optionsが渡されました。")];
        }

        var errors = new List<TranscriptionValidationError>();
        if (!string.Equals(reazon.ModelId.Value, "ja", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("reazonspeech.model.unsupported", $"未対応のReazonSpeechモデルです: {reazon.ModelId}"));
        }
        if (!Enum.IsDefined(reazon.Precision))
        {
            errors.Add(new("reazonspeech.precision.invalid", $"未対応のReazonSpeech精度です: {(int)reazon.Precision}"));
        }
        if (!Enum.IsDefined(reazon.DecodingMethod))
        {
            errors.Add(new("reazonspeech.decoding.invalid", $"未対応のReazonSpeech decoding methodです: {(int)reazon.DecodingMethod}"));
        }
        if (reazon.CpuThreads < 1 || reazon.CpuThreads > Environment.ProcessorCount)
        {
            errors.Add(new(
                "reazonspeech.cpuThreads.invalid",
                $"ReazonSpeech CPU thread数は1～{Environment.ProcessorCount}の範囲で指定してください。現在値: {reazon.CpuThreads}"));
        }
        if (reazon.DecodingMethod == ReazonSpeechDecodingMethod.ModifiedBeamSearch
            && reazon.MaxActivePaths < 1)
        {
            errors.Add(new(
                "reazonspeech.maxActivePaths.invalid",
                $"modified beam searchのmax_active_pathsは1以上で指定してください。現在値: {reazon.MaxActivePaths}"));
        }

        return errors;
    }

    private static string? ReadString(JsonElement settings, string propertyName)
        => settings.ValueKind == JsonValueKind.Object
           && settings.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement settings, string propertyName)
        => settings.ValueKind == JsonValueKind.Object
           && settings.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : null;

    private static ReazonSpeechPrecision ParsePrecision(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => ReazonSpeechPrecision.Int8Fp32,
            "fp32" => ReazonSpeechPrecision.Fp32,
            "int8" => ReazonSpeechPrecision.Int8,
            "int8-fp32" => ReazonSpeechPrecision.Int8Fp32,
            _ => (ReazonSpeechPrecision)(-1)
        };

    private static ReazonSpeechDecodingMethod ParseDecodingMethod(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => ReazonSpeechDecodingMethod.GreedySearch,
            "greedy_search" => ReazonSpeechDecodingMethod.GreedySearch,
            "modified_beam_search" => ReazonSpeechDecodingMethod.ModifiedBeamSearch,
            _ => (ReazonSpeechDecodingMethod)(-1)
        };

    private static string SerializePrecision(ReazonSpeechPrecision precision)
        => precision switch
        {
            ReazonSpeechPrecision.Fp32 => "fp32",
            ReazonSpeechPrecision.Int8 => "int8",
            ReazonSpeechPrecision.Int8Fp32 => "int8-fp32",
            _ => ((int)precision).ToString()
        };

    private static string SerializeDecodingMethod(ReazonSpeechDecodingMethod decodingMethod)
        => decodingMethod switch
        {
            ReazonSpeechDecodingMethod.GreedySearch => "greedy_search",
            ReazonSpeechDecodingMethod.ModifiedBeamSearch => "modified_beam_search",
            _ => ((int)decodingMethod).ToString()
        };
}
