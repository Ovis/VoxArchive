using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper native runtimeとGPU前提条件の検出を一元化する
/// </summary>
public sealed class WhisperRuntimeProbe
{
    /// <summary>
    /// 現在プロセスで利用可能なWhisper runtimeを検出する
    /// </summary>
    public WhisperRuntimeProbeResult Check()
    {
        var loaded = RuntimeOptions.LoadedLibrary;
        return new WhisperRuntimeProbeResult(
            CpuAvailable: RuntimeAssetExists("win-x64", "whisper.dll"),
            CudaAvailable: (RuntimeAssetExists(Path.Combine("cuda", "win-x64"), "whisper.dll")
                            || RuntimeAssetExists(Path.Combine("cuda12", "win-x64"), "whisper.dll"))
                           && HasCudaDriver(),
            VulkanAvailable: RuntimeAssetExists(Path.Combine("vulkan", "win-x64"), "whisper.dll"),
            LoadedRuntime: loaded?.ToString());
    }

    private static bool RuntimeAssetExists(string relativeDirectory, string fileName)
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", relativeDirectory, fileName));

    private static bool HasCudaDriver()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return File.Exists(Path.Combine(windows, "System32", "nvcuda.dll"))
               || (RuntimeInformation.ProcessArchitecture == Architecture.X86
                   && File.Exists(Path.Combine(windows, "SysWOW64", "nvcuda.dll")));
    }
}

/// <summary>
/// Whisper runtime検出結果を保持する
/// </summary>
public sealed record WhisperRuntimeProbeResult(
    bool CpuAvailable,
    bool CudaAvailable,
    bool VulkanAvailable,
    string? LoadedRuntime);
