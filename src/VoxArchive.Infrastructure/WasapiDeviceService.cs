using System.Runtime.Versioning;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WasapiDeviceService : IDeviceService
{
    public Task<IReadOnlyList<AudioDeviceInfo>> GetSpeakerDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = EnumerateDevices(isSpeaker: true, DeviceKind.Speaker, cancellationToken);
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetMicrophoneDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = EnumerateDevices(isSpeaker: false, DeviceKind.Microphone, cancellationToken);
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public async Task<AudioDeviceInfo?> GetDefaultSpeakerDeviceAsync(CancellationToken cancellationToken = default)
    {
        var speakers = await GetSpeakerDevicesAsync(cancellationToken);
        return speakers.FirstOrDefault(x => x.IsDefault);
    }

    public async Task<AudioDeviceInfo?> GetDefaultMicrophoneDeviceAsync(CancellationToken cancellationToken = default)
    {
        var microphones = await GetMicrophoneDevicesAsync(cancellationToken);
        return microphones.FirstOrDefault(x => x.IsDefault);
    }

    private static List<AudioDeviceInfo> EnumerateDevices(bool isSpeaker, DeviceKind kind, CancellationToken cancellationToken)
    {
        var results = new List<AudioDeviceInfo>();
        var types = TryResolveNaudioTypes();
        if (types is null)
        {
            return results;
        }

        try
        {
            var enumerator = Activator.CreateInstance(types.EnumeratorType);
            if (enumerator is null)
            {
                return results;
            }

            try
            {
                var flow = Enum.Parse(types.DataFlowType, isSpeaker ? "Render" : "Capture");
                var state = Enum.Parse(types.DeviceStateType, "Active");
                var role = Enum.Parse(types.RoleType, "Multimedia");

                string defaultDeviceId = string.Empty;
                var defaultDevice = InvokeInstanceMethod(enumerator, "GetDefaultAudioEndpoint", flow, role);
                try
                {
                    defaultDeviceId = GetDeviceId(defaultDevice);
                }
                finally
                {
                    TryDispose(defaultDevice);
                }

                var collection = InvokeInstanceMethod(enumerator, "EnumerateAudioEndPoints", flow, state);
                try
                {
                    foreach (var device in EnumerateCollection(collection))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var deviceId = GetDeviceId(device);
                            if (string.IsNullOrWhiteSpace(deviceId))
                            {
                                continue;
                            }

                            var friendlyName = GetFriendlyName(device);
                            results.Add(new AudioDeviceInfo(
                                DeviceId: deviceId,
                                FriendlyName: string.IsNullOrWhiteSpace(friendlyName) ? deviceId : friendlyName,
                                IsDefault: string.Equals(deviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase),
                                DeviceKind: kind));
                        }
                        finally
                        {
                            TryDispose(device);
                        }
                    }
                }
                finally
                {
                    TryDispose(collection);
                }

                if (results.Count == 0 && defaultDeviceId.Length > 0)
                {
                    results.Add(new AudioDeviceInfo(
                        DeviceId: defaultDeviceId,
                        FriendlyName: defaultDeviceId,
                        IsDefault: true,
                        DeviceKind: kind));
                }
            }
            finally
            {
                TryDispose(enumerator);
            }
        }
        catch
        {
            return results;
        }

        return results;
    }

    private static string GetDeviceId(object? device)
    {
        return GetReadableStringProperty(device, "ID");
    }

    private static string GetFriendlyName(object? device)
    {
        return GetReadableStringProperty(device, "FriendlyName");
    }

    private static string GetReadableStringProperty(object? instance, string propertyName)
    {
        if (instance is null)
        {
            return string.Empty;
        }

        var property = instance.GetType().GetProperty(propertyName);
        if (property is null)
        {
            return string.Empty;
        }

        var value = property.GetValue(instance);
        if (value is string s)
        {
            return s;
        }

        return value?.ToString() ?? string.Empty;
    }

    private static object InvokeInstanceMethod(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName);
        if (method is null)
        {
            throw new MissingMethodException(instance.GetType().FullName, methodName);
        }

        return method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Method {methodName} returned null.");
    }

    private static IEnumerable<object> EnumerateCollection(object? collection)
    {
        if (collection is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    private static void TryDispose(object? instance)
    {
        if (instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static NaudioTypes? TryResolveNaudioTypes()
    {
        var enumeratorType = ResolveType(
            "NAudio.CoreAudioApi.MMDeviceEnumerator, NAudio.Wasapi",
            "NAudio.CoreAudioApi.MMDeviceEnumerator, NAudio.Core");
        var dataFlowType = ResolveType(
            "NAudio.CoreAudioApi.DataFlow, NAudio.Wasapi",
            "NAudio.CoreAudioApi.DataFlow, NAudio.Core");
        var deviceStateType = ResolveType(
            "NAudio.CoreAudioApi.DeviceState, NAudio.Wasapi",
            "NAudio.CoreAudioApi.DeviceState, NAudio.Core");
        var roleType = ResolveType(
            "NAudio.CoreAudioApi.Role, NAudio.Wasapi",
            "NAudio.CoreAudioApi.Role, NAudio.Core");

        if (enumeratorType is null || dataFlowType is null || deviceStateType is null || roleType is null)
        {
            return null;
        }

        return new NaudioTypes(enumeratorType, dataFlowType, deviceStateType, roleType);
    }

    private static Type? ResolveType(params string[] typeNames)
    {
        foreach (var typeName in typeNames)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private sealed record NaudioTypes(
        Type EnumeratorType,
        Type DataFlowType,
        Type DeviceStateType,
        Type RoleType);
}
