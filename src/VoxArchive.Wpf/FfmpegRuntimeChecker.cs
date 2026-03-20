using System.ComponentModel;
using System.Diagnostics;

namespace VoxArchive.Wpf;

internal static class FfmpegRuntimeChecker
{
    public static bool IsAvailable(out string detail)
    {
        detail = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                detail = "ffmpeg プロセスを開始できませんでした。";
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
            detail = "PATH 上で ffmpeg.exe を検出できませんでした。";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}