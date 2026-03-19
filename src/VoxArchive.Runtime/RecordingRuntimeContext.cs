using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Runtime;

public sealed record RecordingRuntimeContext(
    IRecordingService RecordingService,
    ISettingsService SettingsService,
    IDeviceService DeviceService,
    IProcessCatalogService ProcessCatalogService,
    RecordingOptions DefaultOptions);
