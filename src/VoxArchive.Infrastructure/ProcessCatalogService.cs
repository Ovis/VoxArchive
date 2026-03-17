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
                    var executable = SafeGet(() => p.MainModule?.ModuleName) ?? string.Empty;
                    var windowTitle = SafeGet(() => p.MainWindowTitle);

                    list.Add(new ProcessInfo(
                        ProcessId: processId,
                        ApplicationName: appName,
                        ExecutableName: executable,
                        WindowTitle: string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle));
                }
                catch
                {
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
