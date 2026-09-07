using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisperの実行方式をEngine非依存UIから参照・変更するためのcapabilityを提供する
/// </summary>
public sealed class WhisperExecutionModeCapability : ITranscriptionExecutionModeCapability
{
    private static readonly IReadOnlyList<TranscriptionExecutionModeDescriptor> Modes =
    [
        new("auto", "自動"),
        new("cpu", "CPUのみ"),
        new("cuda", "CUDA"),
        new("vulkan", "Vulkan")
    ];

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionExecutionModeDescriptor> GetAvailableModes() => Modes;

    /// <inheritdoc />
    public string GetSelectedMode(ITranscriptionEngineOptions options)
    {
        var whisper = GetWhisperOptions(options);
        return whisper.ExecutionMode switch
        {
            WhisperExecutionMode.Cpu => "cpu",
            WhisperExecutionMode.Cuda => "cuda",
            WhisperExecutionMode.Vulkan => "vulkan",
            _ => "auto"
        };
    }

    /// <inheritdoc />
    public ITranscriptionEngineOptions SelectMode(ITranscriptionEngineOptions options, string modeId)
    {
        var whisper = GetWhisperOptions(options);
        var mode = modeId.Trim().ToLowerInvariant() switch
        {
            "auto" => WhisperExecutionMode.Auto,
            "cpu" => WhisperExecutionMode.Cpu,
            "cuda" => WhisperExecutionMode.Cuda,
            "vulkan" => WhisperExecutionMode.Vulkan,
            _ => throw new ArgumentException($"未対応のWhisper execution modeです: {modeId}", nameof(modeId))
        };
        return whisper with { ExecutionMode = mode };
    }

    private static WhisperEngineOptions GetWhisperOptions(ITranscriptionEngineOptions options)
        => options as WhisperEngineOptions
           ?? throw new ArgumentException("Whisper以外のEngine optionsが渡されました。", nameof(options));
}
