namespace VoxArchive.Domain;

public enum TranscriptionExecutionMode
{
    Auto = 0,
    CpuOnly = 1,

    // Kept only so settings written by older VoxArchive versions can still be deserialized.
    // The settings UI no longer exposes this mode; it is normalized to Auto when loaded.
    [Obsolete("Use Auto. Whisper.net selects CUDA 13, CUDA 12, Vulkan, or CPU at runtime.")]
    CudaPreferred = 2
}
