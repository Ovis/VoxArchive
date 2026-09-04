using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 保存済み文字起こしドキュメントから再文字起こし用のジョブ要求を再構築する
/// </summary>
/// <remarks>
/// 再文字起こしでは過去の実行時に実際に選択されたruntimeではなく、当時要求した設定を再利用する。
/// ドキュメントに存在しない設定だけを現在設定から補完し、旧形式からの再実行でも既存利用者の設定互換性を維持する。
/// </remarks>
public static class TranscriptionRetranscriptionRequestFactory
{
    /// <summary>
    /// 保存済みドキュメントと現在設定から、再文字起こしジョブ用Requestを作成する
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

        var engineId = new TranscriptionEngineId(document.Transcription.Engine);
        if (engineId != TranscriptionEngineId.Whisper)
        {
            throw new NotSupportedException($"現在はWhisper以外の再文字起こしには対応していません: {engineId}");
        }

        var modelId = new TranscriptionModelId(document.Transcription.Model);
        var model = ParseWhisperModel(modelId);
        var usedFallback = false;

        var executionMode = currentOptions.TranscriptionExecutionMode;
        if (TryGetOption(document, "executionMode", out var executionModeId))
        {
            executionMode = executionModeId switch
            {
                "cpu" => TranscriptionExecutionMode.CpuOnly,
                "auto" => TranscriptionExecutionMode.Auto,
                _ => currentOptions.TranscriptionExecutionMode
            };
            usedFallback |= executionModeId is not "cpu" and not "auto";
        }
        else
        {
            usedFallback = true;
        }

        var language = currentOptions.TranscriptionLanguage;
        if (TryGetOption(document, "language", out var storedLanguage) && !string.IsNullOrWhiteSpace(storedLanguage))
        {
            language = storedLanguage;
        }
        else
        {
            usedFallback = true;
        }

        var options = TranscriptionJobOptions.FromRecordingOptions(currentOptions) with
        {
            TranscriptionModel = model,
            TranscriptionExecutionMode = executionMode,
            TranscriptionLanguage = language
        };

        var request = new TranscriptionJobRequest(audioFilePath, options, TranscriptionTrigger.Manual)
        {
            EngineId = engineId,
            ModelId = modelId
        };

        return new RetranscriptionRequestBuildResult(request, isLegacy || usedFallback);
    }

    private static bool TryGetOption(TranscriptionDocument document, string key, out string value)
    {
        if (document.Transcription.Options.TryGetValue(key, out var stored) && stored is not null)
        {
            value = stored;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static TranscriptionModel ParseWhisperModel(TranscriptionModelId modelId) => modelId.Value switch
    {
        "tiny" => TranscriptionModel.Tiny,
        "base" => TranscriptionModel.Base,
        "small" => TranscriptionModel.Small,
        "medium" => TranscriptionModel.Medium,
        "large-v3" => TranscriptionModel.LargeV3,
        _ => throw new NotSupportedException($"未対応のWhisperモデルです: {modelId}")
    };
}

/// <summary>
/// 再文字起こしRequestと、現在設定による補完有無を返す
/// </summary>
public sealed record RetranscriptionRequestBuildResult(
    TranscriptionJobRequest Request,
    bool UsedCurrentSettingsFallback);
