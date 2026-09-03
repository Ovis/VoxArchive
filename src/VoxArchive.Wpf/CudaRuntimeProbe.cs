using System.IO;
using System.Runtime.InteropServices;

namespace VoxArchive.Wpf;

internal static class CudaRuntimeProbe
{
    private const string CudaRuntimeLibraryName = "cudart64_13";

    public static CudaRuntimeProbeResult Check()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return new CudaRuntimeProbeResult(false, "CUDA unavailable (Whisper.net 1.9.0 の CUDA ランタイムは Windows x64 を前提としています)。");
        }

        var nativeRuntimePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "win-x64", "ggml-cuda-whisper.dll");
        if (!File.Exists(nativeRuntimePath))
        {
            return new CudaRuntimeProbeResult(false, $"CUDA unavailable (Whisper.net CUDA runtime asset not found: {nativeRuntimePath})");
        }

        if (!NativeLibrary.TryLoad(CudaRuntimeLibraryName, out var cudartHandle))
        {
            return new CudaRuntimeProbeResult(false, $"CUDA unavailable ({CudaRuntimeLibraryName}.dll をロードできません。CUDA Toolkit 13.x と PATH を確認してください)。");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(cudartHandle, "cudaGetDeviceCount", out var cudaGetDeviceCountAddress))
            {
                return new CudaRuntimeProbeResult(false, "CUDA unavailable (cudaGetDeviceCount を CUDA Runtime から取得できません)。");
            }

            var cudaGetDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCountDelegate>(cudaGetDeviceCountAddress);
            var result = cudaGetDeviceCount(out var deviceCount);
            if (result != 0)
            {
                return new CudaRuntimeProbeResult(false, $"CUDA unavailable (cudaGetDeviceCount failed: error={result})");
            }

            if (deviceCount <= 0)
            {
                return new CudaRuntimeProbeResult(false, "CUDA unavailable (CUDA device was not found)。");
            }

            return new CudaRuntimeProbeResult(
                true,
                $"CUDA available (Whisper.net runtime asset found, {CudaRuntimeLibraryName}.dll loaded, devices: {deviceCount})");
        }
        catch (Exception ex)
        {
            return new CudaRuntimeProbeResult(false, $"CUDA unavailable (runtime probe failed: {ex.Message})");
        }
        finally
        {
            NativeLibrary.Free(cudartHandle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGetDeviceCountDelegate(out int deviceCount);
}

internal sealed record CudaRuntimeProbeResult(bool Available, string Message);
