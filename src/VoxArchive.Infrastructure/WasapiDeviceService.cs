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
            enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out collection);
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out defaultDevice);

            var defaultDeviceId = GetDeviceId(defaultDevice);

            collection.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                collection.Item(i, out var device);
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
        finally
        {
            ReleaseCom(defaultDevice);
            ReleaseCom(collection);
            ReleaseCom(enumerator);
        }

        return results;
    }

    private static string GetDeviceId(IMMDevice device)
    {
        device.GetId(out var ptr);
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
        device.OpenPropertyStore(Stgm.Read, out var propertyStore);
        try
        {
            var key = PropertyKey.DeviceFriendlyName;
            propertyStore.GetValue(ref key, out var value);
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
        void EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0A0F6B7A857")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int deviceCount);
        void Item(int deviceNumber, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, out IntPtr interfacePointer);
        void OpenPropertyStore(Stgm stgmAccess, out IPropertyStore properties);
        void GetId(out IntPtr id);
        void GetState(out DeviceState state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int propertyCount);
        void GetAt(int propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }
}

