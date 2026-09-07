using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// 1回のSilero VAD実行で使用するimmutable設定を保持する
/// </summary>
/// <remarks>
/// persisted設定型をこのprojectへ直接依存させず、Commonから渡されるopaque snapshotをここで解釈する。
/// これによりSilero固有の設定schemaとnative API制約をSilero project内に閉じ込める。
/// </remarks>
public sealed record SileroVadOptions(
    double Threshold,
    int MinimumSpeechDurationMilliseconds,
    int MinimumSilenceDurationMilliseconds,
    int PrePaddingMilliseconds,
    int PostPaddingMilliseconds)
{
    /// <summary>VoxArchiveが採用するSilero標準プロファイル</summary>
    public static SileroVadOptions Default { get; } = new(0.50d, 100, 500, 300, 200);

    /// <summary>
    /// Queue投入時点で固定された設定snapshotをSilero実行設定へ変換する
    /// </summary>
    public static SileroVadOptions FromSnapshot(SpeechRegionDetectorSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Default;
        }
        if (snapshot.SchemaVersion != 1)
        {
            throw new InvalidDataException($"未対応のSilero VAD設定schemaです: {snapshot.SchemaVersion}");
        }

        var root = snapshot.Settings;
        var options = new SileroVadOptions(
            ReadDouble(root, nameof(Threshold), Default.Threshold),
            ReadInt(root, nameof(MinimumSpeechDurationMilliseconds), Default.MinimumSpeechDurationMilliseconds),
            ReadInt(root, nameof(MinimumSilenceDurationMilliseconds), Default.MinimumSilenceDurationMilliseconds),
            ReadInt(root, nameof(PrePaddingMilliseconds), Default.PrePaddingMilliseconds),
            ReadInt(root, nameof(PostPaddingMilliseconds), Default.PostPaddingMilliseconds));
        Validate(options);
        return options;
    }

    /// <summary>
    /// sherpa-onnxのSilero設定として実行可能な値か検証する
    /// </summary>
    public static void Validate(SileroVadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Threshold < 0.01d || options.Threshold >= 1d)
            throw new ArgumentOutOfRangeException(nameof(options), "Thresholdは0.01以上1.0未満である必要があります。");
        if (options.MinimumSpeechDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum Speech Durationは正数である必要があります。");
        if (options.MinimumSilenceDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum Silence Durationは正数である必要があります。");
        if (options.PrePaddingMilliseconds < 0 || options.PostPaddingMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Paddingは0以上である必要があります。");
    }

    private static double ReadDouble(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : fallback;

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
}
