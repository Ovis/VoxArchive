using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace VoxArchive.Wpf;

internal static class FfmpegRuntimeChecker
{
    private const string FfmpegFileName = "ffmpeg.exe";

    public static bool IsAvailable(string? configuredPath, out string detail)
    {
        return IsAvailable(configuredPath, out detail, out _);
    }

    public static bool IsAvailable(string? configuredPath, out string detail, out string resolvedExecutablePath)
    {
        detail = string.Empty;
        resolvedExecutablePath = string.Empty;

        var candidates = BuildCandidates(configuredPath);
        foreach (var candidate in candidates)
        {
            if (TryProbe(candidate, out detail))
            {
                resolvedExecutablePath = candidate;
                return true;
            }
        }

        // 最終フォールバックとして OS の PATH 解決に委ねる。
        if (TryProbe("ffmpeg", out detail))
        {
            resolvedExecutablePath = "ffmpeg";
            return true;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "ffmpeg.exe を検出できませんでした。設定画面で実行ファイルパスを指定してください。";
        }

        return false;
    }

    private static bool TryProbe(string executablePath, out string detail)
    {
        detail = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                detail = $"ffmpeg プロセスを開始できませんでした。({executablePath})";
                return false;
            }

            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // タイムアウト時の後処理失敗は検知結果に影響しない。
                }
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            detail = $"ffmpeg.exe を検出できませんでした。({executablePath})";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> BuildCandidates(string? configuredPath)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfValid(ResolveConfiguredPath(configuredPath), results, seen);
        AddIfValid(Path.Combine(AppContext.BaseDirectory, FfmpegFileName), results, seen);

        foreach (var pathCandidate in EnumerateFromPath())
        {
            AddIfValid(pathCandidate, results, seen);
        }

        return results;
    }

    private static string? ResolveConfiguredPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var trimmed = configuredPath.Trim().Trim('"');
        if (Path.IsPathFullyQualified(trimmed))
        {
            if (Directory.Exists(trimmed))
            {
                return Path.Combine(trimmed, FfmpegFileName);
            }

            return trimmed;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try
            {
                if (!Path.IsPathFullyQualified(entry))
                {
                    continue;
                }

                full = Path.Combine(entry, FfmpegFileName);
            }
            catch
            {
                continue;
            }

            yield return full;
        }
    }

    private static void AddIfValid(string? path, ICollection<string> list, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                return;
            }

            if (seen.Add(full))
            {
                list.Add(full);
            }
        }
        catch
        {
            // 参照不能な PATH 項目はスキップする。
        }
    }
}