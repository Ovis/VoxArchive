using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Job Admission時にWhisperの明示backend要求を検証する
/// </summary>
public sealed class WhisperExecutionValidator(WhisperRuntimeProbe runtimeProbe)
    : ITranscriptionEngineExecutionValidator
{
    /// <inheritdoc />
    public Task<IReadOnlyList<TranscriptionValidationError>> ValidateAsync(
        ITranscriptionEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options is not WhisperEngineOptions whisper)
        {
            return Task.FromResult<IReadOnlyList<TranscriptionValidationError>>([
                new("whisper.options.type", "Whisper以外のEngine optionsが渡されました。")
            ]);
        }

        var status = runtimeProbe.Check();
        var available = whisper.ExecutionMode switch
        {
            WhisperExecutionMode.Auto => status.CpuAvailable || status.CudaAvailable || status.VulkanAvailable,
            WhisperExecutionMode.Cpu => status.CpuAvailable,
            WhisperExecutionMode.Cuda => status.CudaAvailable,
            WhisperExecutionMode.Vulkan => status.VulkanAvailable,
            _ => false
        };

        if (available)
        {
            return Task.FromResult<IReadOnlyList<TranscriptionValidationError>>([]);
        }

        // 明示backendをCPUへ置き換えると設定した実行方式と実際の処理が食い違うため、
        // Auto以外はAdmissionで明確に実行不可とする。
        return Task.FromResult<IReadOnlyList<TranscriptionValidationError>>([
            new("whisper.runtime.unavailable", $"要求したWhisper実行方式を利用できません: {whisper.ExecutionMode}")
        ]);
    }
}
