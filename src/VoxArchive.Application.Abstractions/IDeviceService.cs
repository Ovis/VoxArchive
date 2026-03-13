using VoxArchive.Domain;

namespace VoxArchive.Application.Abstractions;

public interface IDeviceService
{
    Task<IReadOnlyList<AudioDeviceInfo>> GetSpeakerDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> GetMicrophoneDevicesAsync(CancellationToken cancellationToken = default);
    Task<AudioDeviceInfo?> GetDefaultSpeakerDeviceAsync(CancellationToken cancellationToken = default);
    Task<AudioDeviceInfo?> GetDefaultMicrophoneDeviceAsync(CancellationToken cancellationToken = default);
}
