using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

public interface ISettingsService
{
    Task<RecordingOptions> LoadRecordingOptionsAsync(CancellationToken cancellationToken = default);
    Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default);
}
