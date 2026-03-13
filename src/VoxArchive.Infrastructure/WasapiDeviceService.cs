using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WasapiDeviceService : IDeviceService
{
    public Task<IReadOnlyList<AudioDeviceInfo>> GetSpeakerDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = EnumerateDevices(EDataFlow.eRender, DeviceKind.Speaker, cancellationToken);
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetMicrophoneDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = EnumerateDevices(EDataFlow.eCapture, DeviceKind.Microphone, cancellationToken);
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

    private static List<AudioDeviceInfo> EnumerateDevices(EDataFlow flow, DeviceKind kind, CancellationToken cancellationToken)
    {
        var results = new List<AudioDeviceInfo>();

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out defaultDevice));
            var defaultDeviceId = GetDeviceId(defaultDevice);

            if (TryEnumAudioEndpoints(enumerator, flow, out collection) && collection is not null)
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                for (uint i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
                    try
                    {
                        var deviceId = GetDeviceId(device);
                        var friendlyName = GetFriendlyName(device);
                        results.Add(new AudioDeviceInfo(
                            DeviceId: deviceId,
                            FriendlyName: string.IsNullOrWhiteSpace(friendlyName) ? deviceId : friendlyName,
                            IsDefault: string.Equals(deviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase),
                            DeviceKind: kind));
                    }
                    finally
                    {
                        ReleaseCom(device);
                    }
                }
            }
            else
            {
                // 一部環境で EnumAudioEndpoints の COM マーシャリングに失敗するため既定デバイスへフォールバック。
                var friendlyName = GetFriendlyName(defaultDevice);
                results.Add(new AudioDeviceInfo(
                    DeviceId: defaultDeviceId,
                    FriendlyName: string.IsNullOrWhiteSpace(friendlyName) ? defaultDeviceId : friendlyName,
                    IsDefault: true,
                    DeviceKind: kind));
            }
        }
        finally
        {
            ReleaseCom(defaultDevice);
            ReleaseCom(collection);
            ReleaseCom(enumerator);
        }

        return results;
    }

    private static bool TryEnumAudioEndpoints(IMMDeviceEnumerator enumerator, EDataFlow flow, out IMMDeviceCollection? collection)
    {
        collection = null;

        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, (uint)DeviceState.Active, out collection));
            return collection is not null;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private static string GetDeviceId(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var ptr));
        try
        {
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore((uint)Stgm.Read, out var propertyStore));
        try
        {
            var key = PropertyKey.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, out var value));
            try
            {
                return value.GetString();
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            ReleaseCom(propertyStore);
        }
    }

    private static void ReleaseCom(object? instance)
    {
        if (instance is null)
        {
            return;
        }

        Marshal.ReleaseComObject(instance);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
        EDataFlowEnumCount
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
        ERoleEnumCount
    }

    [Flags]
    private enum DeviceState : uint
    {
        Active = 0x00000001,
        Disabled = 0x00000002,
        NotPresent = 0x00000004,
        Unplugged = 0x00000008,
        All = 0x0000000F
    }

    private enum Stgm : uint
    {
        Read = 0x00000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public static PropertyKey DeviceFriendlyName => new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pointerValue;
        public int intValue;

        public string GetString()
        {
            const ushort VT_LPWSTR = 31;
            if (vt != VT_LPWSTR || pointerValue == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(pointerValue) ?? string.Empty;
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0A0F6B7A857")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint deviceCount);

        [PreserveSig]
        int Item(uint deviceNumber, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr activationParams, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant pv);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant pv);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }
}
