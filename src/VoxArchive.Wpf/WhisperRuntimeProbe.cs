using System.IO;

namespace VoxArchive.Wpf;

internal static class WhisperRuntimeProbe
{
    public static WhisperRuntimeProbeResult Check()
    {
        var details = new List<string>
        {
            DescribeRuntime("CUDA 13", "cuda", "ggml-cuda-whisper.dll"),
            DescribeRuntime("CUDA 12", "cuda12", "ggml-cuda-whisper.dll"),
            DescribeRuntime("Vulkan", "vulkan", "ggml-vulkan-whisper.dll"),
            DescribeRuntime("CPU", "cpu", "whisper.dll")
        };

        details.Add("自動モードでは Whisper.net が CUDA 13 → CUDA 12 → Vulkan → CPU の順で利用可能なランタイムを選択します。");
        details.Add("ここでは同梱ランタイムだけを確認します。実際の利用可否と選択結果は Whisper.net の実行時プローブで決定されます。");

        return new WhisperRuntimeProbeResult("Whisper.net ランタイム選択: 自動", details);
    }

    private static string DescribeRuntime(string displayName, string directoryHint, string fileName)
    {
        var runtimesRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimesRoot))
        {
            return $"{displayName}: ランタイムアセット未検出";
        }

        try
        {
            var found = Directory.EnumerateFiles(runtimesRoot, fileName, SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(directoryHint, StringComparison.OrdinalIgnoreCase));
            return found is null
                ? $"{displayName}: ランタイムアセット未検出"
                : $"{displayName}: ランタイムアセット同梱";
        }
        catch (Exception ex)
        {
            return $"{displayName}: 確認失敗 ({ex.Message})";
        }
    }
}

internal sealed record WhisperRuntimeProbeResult(string Summary, IReadOnlyList<string> Details);
