using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeechのprecision、decoding、beam、CPU thread設定を安定IDで公開する
/// </summary>
public sealed class ReazonSpeechAdvancedSettingsCapability : ITranscriptionEngineAdvancedSettingsCapability
{
    public const string PrecisionKey = "precision";
    public const string DecodingMethodKey = "decodingMethod";
    public const string MaxActivePathsKey = "maxActivePaths";
    public const string CpuThreadsKey = "cpuThreads";

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => ReazonSpeechEngineIdentity.EngineId;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetValues(ITranscriptionEngineOptions options)
    {
        var reazon = RequireOptions(options);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrecisionKey] = reazon.Precision switch
            {
                ReazonSpeechPrecision.Fp32 => "fp32",
                ReazonSpeechPrecision.Int8 => "int8",
                ReazonSpeechPrecision.Int8Fp32 => "int8-fp32",
                _ => ((int)reazon.Precision).ToString()
            },
            [DecodingMethodKey] = reazon.DecodingMethod switch
            {
                ReazonSpeechDecodingMethod.GreedySearch => "greedy_search",
                ReazonSpeechDecodingMethod.ModifiedBeamSearch => "modified_beam_search",
                _ => ((int)reazon.DecodingMethod).ToString()
            },
            [MaxActivePathsKey] = reazon.MaxActivePaths.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CpuThreadsKey] = reazon.CpuThreads.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <inheritdoc />
    public ITranscriptionEngineOptions ApplyValues(
        ITranscriptionEngineOptions options,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var reazon = RequireOptions(options);
        return reazon with
        {
            Precision = ParsePrecision(GetRequired(values, PrecisionKey)),
            DecodingMethod = ParseDecodingMethod(GetRequired(values, DecodingMethodKey)),
            MaxActivePaths = ParseInt(GetRequired(values, MaxActivePathsKey), MaxActivePathsKey),
            CpuThreads = ParseInt(GetRequired(values, CpuThreadsKey), CpuThreadsKey)
        };
    }

    private static ReazonSpeechEngineOptions RequireOptions(ITranscriptionEngineOptions options)
        => options as ReazonSpeechEngineOptions
           ?? throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException($"ReazonSpeech詳細設定 '{key}' がありません。", nameof(values));

    private static ReazonSpeechPrecision ParsePrecision(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "fp32" => ReazonSpeechPrecision.Fp32,
            "int8" => ReazonSpeechPrecision.Int8,
            "int8-fp32" => ReazonSpeechPrecision.Int8Fp32,
            _ => throw new ArgumentException($"未対応のReazonSpeech precisionです: {value}")
        };

    private static ReazonSpeechDecodingMethod ParseDecodingMethod(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "greedy_search" => ReazonSpeechDecodingMethod.GreedySearch,
            "modified_beam_search" => ReazonSpeechDecodingMethod.ModifiedBeamSearch,
            _ => throw new ArgumentException($"未対応のReazonSpeech decoding methodです: {value}")
        };

    private static int ParseInt(string value, string key)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"ReazonSpeech詳細設定 '{key}' は整数である必要があります: {value}");
}
