using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Runtime;

public sealed record RecordingRuntimeContext(
    IRecordingService RecordingService,
    ISettingsService SettingsService,
    IDeviceService DeviceService,
    RecordingOptions DefaultOptions);
