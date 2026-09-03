namespace VoxArchive.Wpf;

// Temporary compatibility bridge for SettingsWindow while its environment-check presentation
// remains expressed through the older CudaRuntimeProbeResult shape.
internal static class CudaRuntimeProbe
{
    public static CudaRuntimeProbeResult Check()
    {
        var result = WhisperRuntimeProbe.Check();
        var message = string.Join(Environment.NewLine, new[] { result.Summary }.Concat(result.Details));
        return new CudaRuntimeProbeResult(true, message);
    }
}

internal sealed record CudaRuntimeProbeResult(bool Available, string Message);
