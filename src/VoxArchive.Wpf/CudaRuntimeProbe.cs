namespace VoxArchive.Wpf;

// Compatibility bridge for the existing settings window. The implementation now uses the
// unified Whisper runtime probe and can report CUDA 13, CUDA 12, Vulkan, or CPU fallback.
internal static class CudaRuntimeProbe
{
    public static CudaRuntimeProbeResult Check()
    {
        var result = WhisperRuntimeProbe.Check();
        var message = string.Join(Environment.NewLine, new[] { result.Summary }.Concat(result.Details));
        return new CudaRuntimeProbeResult(result.GpuAvailable, message);
    }
}

internal sealed record CudaRuntimeProbeResult(bool Available, string Message);
