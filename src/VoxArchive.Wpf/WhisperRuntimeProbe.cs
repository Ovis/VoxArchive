using System.IO;
using System.Runtime.InteropServices;

namespace VoxArchive.Wpf;

internal static class WhisperRuntimeProbe
{
    public static WhisperRuntimeProbeResult Check()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return new WhisperRuntimeProbeResult(false,
                "GPUアクセラレーション: 利用不可",
                new[] { "Whisper.net のGPUランタイムはVoxArchiveではWindows x64をサポート対象としています。", "CPU: 利用可能" });
        }

        var details = new List<string>();
        var cuda13 = ProbeCuda("CUDA 13", "cudart64_13");
        var cuda12 = ProbeCuda("CUDA 12", "cudart64_12");
        var vulkan = ProbeVulkan();

        details.Add(cuda13.Message);
        details.Add(cuda12.Message);
        details.Add(vulkan.Message);
        details.Add("CPU: 利用可能");
        details.Add("自動モードでは Whisper.net が CUDA 13 → CUDA 12 → Vulkan → CPU の順で利用可能なランタイムを選択します。");

        var gpuAvailable = cuda13.Available || cuda12.Available || vulkan.Available;
        return new WhisperRuntimeProbeResult(
            gpuAvailable,
            gpuAvailable ? "GPUアクセラレーション: 利用可能" : "GPUアクセラレーション: 利用不可（CPUへフォールバック）",
            details);
    }

    private static RuntimeProbeEntry ProbeCuda(string displayName, string cudartLibraryName)
    {
        if (!NativeLibrary.TryLoad(cudartLibraryName, out var handle))
        {
            return new RuntimeProbeEntry(false, $"{displayName}: 利用不可 ({cudartLibraryName}.dll をロードできません)");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "cudaGetDeviceCount", out var address))
            {
                return new RuntimeProbeEntry(false, $"{displayName}: 利用不可 (cudaGetDeviceCount を取得できません)");
            }

            var getDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCountDelegate>(address);
            var result = getDeviceCount(out var deviceCount);
            return result == 0 && deviceCount > 0
                ? new RuntimeProbeEntry(true, $"{displayName}: 利用可能 (devices: {deviceCount})")
                : new RuntimeProbeEntry(false, $"{displayName}: 利用不可 (cudaGetDeviceCount error={result}, devices={deviceCount})");
        }
        catch (Exception ex)
        {
            return new RuntimeProbeEntry(false, $"{displayName}: 判定失敗 ({ex.Message})");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static RuntimeProbeEntry ProbeVulkan()
    {
        var runtimeAsset = FindRuntimeAsset("vulkan", "ggml-vulkan-whisper.dll");
        if (runtimeAsset is null)
        {
            return new RuntimeProbeEntry(false, "Vulkan: 利用不可 (Whisper.net Vulkan runtime asset がありません)");
        }

        if (!NativeLibrary.TryLoad("vulkan-1", out var handle))
        {
            return new RuntimeProbeEntry(false, "Vulkan: 利用不可 (vulkan-1.dll をロードできません)");
        }

        NativeLibrary.Free(handle);
        return new RuntimeProbeEntry(true, "Vulkan: ランタイム候補あり (Vulkan loader を検出)");
    }

    private static string? FindRuntimeAsset(string runtimeDirectory, string fileName)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeDirectory, "win-x64", fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            return Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGetDeviceCountDelegate(out int deviceCount);

    private sealed record RuntimeProbeEntry(bool Available, string Message);
}

internal sealed record WhisperRuntimeProbeResult(bool GpuAvailable, string Summary, IReadOnlyList<string> Details);
