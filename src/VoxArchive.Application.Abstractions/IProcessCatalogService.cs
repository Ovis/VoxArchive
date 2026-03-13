using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

public interface IProcessCatalogService
{
    Task<IReadOnlyList<ProcessInfo>> GetRunningProcessesAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(int processId, CancellationToken cancellationToken = default);
}
