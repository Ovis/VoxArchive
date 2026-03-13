namespace VoxArchive.Domain;

public sealed record AudioDeviceInfo(
    string DeviceId,
    string FriendlyName,
    bool IsDefault,
    DeviceKind DeviceKind);
