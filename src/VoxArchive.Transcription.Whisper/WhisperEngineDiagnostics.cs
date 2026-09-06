using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper runtimeの状態をEngine非依存診断DTOへ投影する
/// </summary>
public sealed class WhisperEngineDiagnostics(WhisperRuntimeProbe runtimeProbe) : ITranscriptionEngineDiagnostics
{
    /// <inheritdoc />
    public Task<IReadOnlyList<TranscriptionDiagnosticItem>> DiagnoseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = runtimeProbe.Check();
        IReadOnlyList<TranscriptionDiagnosticItem> items =
        [
            Create("whisper.runtime.cpu", "CPU", status.CpuAvailable),
            Create("whisper.runtime.cuda", "CUDA", status.CudaAvailable),
            Create("whisper.runtime.vulkan", "Vulkan", status.VulkanAvailable),
            new(
                "whisper.runtime.loaded",
                status.LoadedRuntime is null ? "Whisper runtimeはまだロードされていません。" : $"ロード済みruntime: {status.LoadedRuntime}",
                TranscriptionDiagnosticSeverity.Information)
        ];
        return Task.FromResult(items);
    }

    private static TranscriptionDiagnosticItem Create(string code, string name, bool available)
        => new(
            code,
            $"{name}: {(available ? "利用可能" : "利用不可")}",
            available ? TranscriptionDiagnosticSeverity.Information : TranscriptionDiagnosticSeverity.Warning);
}
