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

        details.Add("自動モードでは Whisper.net が CUDA 13 → CUDA 12 → Vulkan → CPU の順で利用可能なランタイムを選択します。");
        details.Add("ここでは同梱ランタイムだけを確認します。実際の利用可否と選択結果は Whisper.net の実行時プローブで決定されます。");

        return new WhisperRuntimeProbeResult("Whisper.net ランタイム選択: 自動", details);
    }

    private static string DescribeRuntime(string displayName, string relativeDirectory, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", relativeDirectory, fileName);
        return File.Exists(path)
            ? $"{displayName}: ランタイムアセット同梱"
            : $"{displayName}: ランタイムアセット未検出";
    }
}

internal sealed record WhisperRuntimeProbeResult(string Summary, IReadOnlyList<string> Details);
