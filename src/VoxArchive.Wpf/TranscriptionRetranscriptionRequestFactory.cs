using System.Text.Json;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 保存済み文字起こしドキュメントから再文字起こし用の設定snapshotを再構築する
/// </summary>
/// <remarks>
/// 再文字起こしでは過去の実行時に実際に選択されたruntimeではなく、当時要求した設定を再利用する。
/// ドキュメントに存在しない設定だけを現在設定から補完し、Application Admissionへ通常の手動ジョブとして渡す。
/// </remarks>
public static class TranscriptionRetranscriptionRequestFactory
{
    /// <summary>
    /// 保存済みドキュメントと現在設定から、再文字起こし用のimmutable設定snapshotを作成する
    /// </summary>
    public static RetranscriptionRequestBuildResult Create(
        string audioFilePath,
        TranscriptionDocument document,
        RecordingOptions currentOptions,
        bool isLegacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(currentOptions);

        var engineId = document.Transcription.Engine?.Trim().ToLowerInvariant();
        if (!string.Equals(engineId, "whisper", StringComparison.Ordinal))
        {
            // 現行の旧canonical documentにはWhisper再実行に必要なrequested optionsだけが保存されている。
            // ReazonSpeech等へ推測で変換すると再現性を失うため、保存形式が十分になるまでは明示的に拒否する。
            throw new NotSupportedException($"現在はWhisper以外の旧文字起こし結果の再文字起こしには対応していません: {engineId}");
        }

        var modelId = string.IsNullOrWhiteSpace(document.Transcription.Model)
            ? ReadCurrentWhisperSetting(currentOptions, "modelId") ?? "small"
            : document.Transcription.Model.Trim().ToLowerInvariant();
        var usedFallback = string.IsNullOrWhiteSpace(document.Transcription.Model);

        var executionMode = ReadCurrentWhisperSetting(currentOptions, "executionMode") ?? "auto";
        if (TryGetOption(document, "executionMode", out var storedExecutionMode)
            && storedExecutionMode is "cpu" or "auto" or "cuda" or "vulkan")
        {
            executionMode = storedExecutionMode;
        }
        else
        {
            usedFallback = true;
        }

        var language = currentOptions.Transcription.PreferredLanguage;
        if (TryGetOption(document, "language", out var storedLanguage))
        {
            language = storedLanguage;
        }
        else
        {
            usedFallback = true;
        }

        // 未知Engineのsettings blobを消さないよう既存Dictionaryを複製し、再実行対象Engineだけを置き換える。
        var engines = new Dictionary<string, TranscriptionEngineSettings>(currentOptions.Transcription.Engines, StringComparer.OrdinalIgnoreCase)
        {
            ["whisper"] = new TranscriptionEngineSettings
            {
                SchemaVersion = 1,
                Settings = JsonSerializer.SerializeToElement(new
                {
                    modelId,
                    executionMode
                })
            }
        };

        var transcription = currentOptions.Transcription with
        {
            DefaultEngine = "whisper",
            PreferredLanguage = language,
            Engines = engines
        };

        return new RetranscriptionRequestBuildResult(
            currentOptions with { Transcription = transcription },
            isLegacy || usedFallback);
    }

    private static bool TryGetOption(TranscriptionDocument document, string key, out string value)
    {
        if (document.Transcription.Options.TryGetValue(key, out var stored) && stored is not null)
        {
            value = stored.Trim().ToLowerInvariant();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? ReadCurrentWhisperSetting(RecordingOptions options, string propertyName)
    {
        if (!options.Transcription.Engines.TryGetValue("whisper", out var settings)
            || settings.Settings.ValueKind != JsonValueKind.Object
            || !settings.Settings.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString()?.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// 再文字起こし用設定snapshotと、現在設定による補完有無を返す
/// </summary>
public sealed record RetranscriptionRequestBuildResult(
    RecordingOptions Options,
    bool UsedCurrentSettingsFallback);
