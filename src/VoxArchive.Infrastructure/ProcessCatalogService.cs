using System.Diagnostics;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class ProcessCatalogService : IProcessCatalogService
{
    public Task<IReadOnlyList<ProcessInfo>> GetRunningProcessesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ProcessInfo>>(() =>
        {
            var processes = Process.GetProcesses();
            var list = new List<ProcessInfo>(processes.Length);

            foreach (var p in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var processId = p.Id;
                    var appName = SafeGet(() => p.ProcessName) ?? string.Empty;
                    // MainModuleは保護プロセスや終了直後のプロセスでWin32Exceptionを投げる。
                    // 実行ファイル名は一覧表示専用なので、既に取得済みのProcessNameから組み立てる。
                    var executable = string.IsNullOrWhiteSpace(appName) ? string.Empty : $"{appName}.exe";
                    var windowTitle = SafeGet(() => p.MainWindowTitle);

                    list.Add(new ProcessInfo(
                        ProcessId: processId,
                        ApplicationName: appName,
                        ExecutableName: executable,
                        WindowTitle: string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle));
                }
                catch
                {
                    // 権限不足や終了済みプロセスは列挙対象から除外する。
                }
                finally
                {
                    p.Dispose();
                }
            }

            return list
                .OrderBy(x => x.ApplicationName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ProcessId)
                .ToList();
        }, cancellationToken);
    }

    public Task<bool> ExistsAsync(int processId, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var p = Process.GetProcessById(processId);
                using (p)
                {
                    return !p.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }
}
