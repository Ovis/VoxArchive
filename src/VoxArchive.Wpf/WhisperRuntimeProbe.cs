using System.IO;

namespace VoxArchive.Wpf;

internal static class WhisperRuntimeProbe
{
    public static WhisperRuntimeProbeResult Check()
    {
        var details = new List<string>
        {
            DescribeRuntime("CUDA 13", Path.Combine("cuda", "win-x64"), "whisper.dll"),
            DescribeRuntime("CUDA 12", Path.Combine("cuda12", "win-x64"), "whisper.dll"),
            DescribeRuntime("Vulkan", Path.Combine("vulkan", "win-x64"), "whisper.dll"),
            DescribeRuntime("CPU", "win-x64", "whisper.dll")
        };

        details.Add("文字起こしの実行時には、CUDA 13、CUDA 12、Vulkan、CPUの中から利用可能な処理方式が自動的に選択されます。");

        return new WhisperRuntimeProbeResult("文字起こし処理方式: 自動選択", details);
    }

    private static string DescribeRuntime(string displayName, string relativeDirectory, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", relativeDirectory, fileName);
        return File.Exists(path)
            ? $"{displayName}: 利用可能"
            : $"{displayName}: 利用不可";
    }
}

internal sealed record WhisperRuntimeProbeResult(string Summary, IReadOnlyList<string> Details);
