using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

[SupportedOSPlatform("windows")]
public sealed class ProcessLoopbackCaptureService(ILogger<ProcessLoopbackCaptureService> logger) : IProcessLoopbackCaptureService
{
    // Process Loopback 用の仮想デバイス ID。
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const ushort VtBlob = 0x0041;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _targetProcessId;
    private bool _running;

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    private IAudioClientNative? _audioClient;

    // IAudioCaptureClient は RCW キャスト環境差異が出るため、生ポインタ+vtable で呼び出す。
    private IntPtr _captureClientPtr;
    private CaptureGetBufferDelegate? _captureGetBuffer;
    private CaptureReleaseBufferDelegate? _captureReleaseBuffer;
    private CaptureGetNextPacketSizeDelegate? _captureGetNextPacketSize;

    private WaveFormat? _waveFormat;
    private int _channels;
    private int _bitsPerSample;
    private bool _isFloat;
    private int _bytesPerFrame;

    public event EventHandler<CaptureChunk>? ChunkCaptured;
    public event EventHandler? TargetProcessExited;

    public async Task StartAsync(int targetProcessId, int sampleRate, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetProcessId);

        await StopAsync(cancellationToken);

        _targetProcessId = targetProcessId;
        _running = true;

        _audioClient = await ActivateProcessLoopbackAudioClientAsync(targetProcessId, cancellationToken);

        _waveFormat = InitializeAudioClientWithFallbackFormats(_audioClient, sampleRate);
        _channels = Math.Max(1, _waveFormat.Channels);
        _bitsPerSample = Math.Max(16, _waveFormat.BitsPerSample);
        _isFloat = _waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        _bytesPerFrame = Math.Max(1, (_bitsPerSample / 8) * _channels);

        var captureClientGuid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        var getServiceHr = _audioClient.GetService(ref captureClientGuid, out var captureClientPtr);
        Marshal.ThrowExceptionForHR(getServiceHr);
        InitializeCaptureClient(captureClientPtr);

        var startHr = _audioClient.Start();
        Marshal.ThrowExceptionForHR(startHr);

        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token), _captureCts.Token);

        _monitorCts?.Cancel();
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorTargetProcessAsync(_monitorCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;

        if (_captureCts is not null)
        {
            await _captureCts.CancelAsync();
            _captureCts.Dispose();
            _captureCts = null;
        }

        if (_captureTask is not null)
        {
            try
            {
                await _captureTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 停止要求で待機が中断された場合は正常系として扱う。
            }
            catch
            {
                // 既に停止済み/解放済みの場合があるため握りつぶす。
            }

            _captureTask = null;
        }

        if (_audioClient is not null)
        {
            try
            {
                _audioClient.Stop();
            }
            catch
            {
                // 既に停止済み/解放済みの場合があるため握りつぶす。
            }
        }

        ReleaseCaptureClient();
        ReleaseComObject(ref _audioClient);

        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync();
            _monitorCts.Dispose();
            _monitorCts = null;
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 停止要求で待機が中断された場合は正常系として扱う。
            }

            _monitorTask = null;
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               _running &&
               _captureClientPtr != IntPtr.Zero &&
               _captureGetNextPacketSize is not null)
        {
            try
            {
                var hr = _captureGetNextPacketSize(_captureClientPtr, out var packetFrames);
                Marshal.ThrowExceptionForHR(hr);

                if (packetFrames <= 0)
                {
                    await Task.Delay(5, cancellationToken);
                    continue;
                }

                while (packetFrames > 0 && _running && !cancellationToken.IsCancellationRequested)
                {
                    hr = _captureGetBuffer!(_captureClientPtr,
                        out var dataPointer,
                        out var framesAvailable,
                        out var flags,
                        out _,
                        out _);
                    Marshal.ThrowExceptionForHR(hr);

                    float[] mono;
                    if ((flags & AudioClientBufferFlags.Silent) != 0 || dataPointer == IntPtr.Zero)
                    {
                        mono = new float[(int)framesAvailable];
                    }
                    else
                    {
                        var bytesRecorded = Math.Max(0, (int)framesAvailable * _bytesPerFrame);
                        var buffer = new byte[bytesRecorded];
                        Marshal.Copy(dataPointer, buffer, 0, bytesRecorded);
                        mono = NAudioCaptureUtils.ToMonoFloat(buffer, bytesRecorded, _channels, _bitsPerSample, _isFloat);
                    }

                    _captureReleaseBuffer!(_captureClientPtr, framesAvailable);

                    if (mono.Length > 0 && _running)
                    {
                        ChunkCaptured?.Invoke(this, new CaptureChunk(mono, _waveFormat?.SampleRate ?? 48_000, DateTimeOffset.UtcNow));
                    }

                    hr = _captureGetNextPacketSize(_captureClientPtr, out packetFrames);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ProcessLoopback capture error.");

                if (!IsProcessAlive(_targetProcessId))
                {
                    _running = false;
                    TargetProcessExited?.Invoke(this, EventArgs.Empty);
                    break;
                }

                try
                {
                    await Task.Delay(30, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<IAudioClientNative> ActivateProcessLoopbackAudioClientAsync(int targetProcessId, CancellationToken cancellationToken)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)targetProcessId,
                ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            }
        };

        var activationParamsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        var activationParamsPtr = Marshal.AllocCoTaskMem(activationParamsSize);
        var propVariantPtr = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(activationParams, activationParamsPtr, false);

            var propVariant = new PROPVARIANT
            {
                vt = VtBlob,
                blob = new BLOB
                {
                    cbSize = (uint)activationParamsSize,
                    pBlobData = activationParamsPtr
                }
            };

            propVariantPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT>());
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var completionHandler = new ActivateAudioInterfaceCompletionHandler();
            var iidAudioClient = typeof(IAudioClientNative).GUID;

            var activateHr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref iidAudioClient,
                propVariantPtr,
                completionHandler,
                out _);
            Marshal.ThrowExceptionForHR(activateHr);

            var result = await completionHandler.WaitAsync(cancellationToken);
            Marshal.ThrowExceptionForHR(result.activateResult);

            var audioClient = ResolveAudioClientInterface(result.activatedInterfacePtr);
            if (audioClient is null)
            {
                throw new InvalidCastException("Failed to acquire IAudioClientNative from activated interface.");
            }

            return audioClient;
        }
        finally
        {
            if (propVariantPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(propVariantPtr);
            }

            if (activationParamsPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(activationParamsPtr);
            }
        }
    }

    private void InitializeCaptureClient(IntPtr captureClientPtr)
    {
        ReleaseCaptureClient();

        if (captureClientPtr == IntPtr.Zero)
        {
            throw new InvalidCastException("Failed to acquire IAudioCaptureClient from audio client service.");
        }

        _captureClientPtr = captureClientPtr;

        var vtable = Marshal.ReadIntPtr(_captureClientPtr);
        if (vtable == IntPtr.Zero)
        {
            throw new InvalidCastException("Capture client vtable is null.");
        }

        // IAudioCaptureClient の vtable: IUnknown(0-2), GetBuffer(3), ReleaseBuffer(4), GetNextPacketSize(5)
        var getBufferPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var releaseBufferPtr = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        var getNextPacketSizePtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);

        if (getBufferPtr == IntPtr.Zero || releaseBufferPtr == IntPtr.Zero || getNextPacketSizePtr == IntPtr.Zero)
        {
            throw new InvalidCastException("Capture client method pointers are invalid.");
        }

        _captureGetBuffer = Marshal.GetDelegateForFunctionPointer<CaptureGetBufferDelegate>(getBufferPtr);
        _captureReleaseBuffer = Marshal.GetDelegateForFunctionPointer<CaptureReleaseBufferDelegate>(releaseBufferPtr);
        _captureGetNextPacketSize = Marshal.GetDelegateForFunctionPointer<CaptureGetNextPacketSizeDelegate>(getNextPacketSizePtr);
    }

    private void ReleaseCaptureClient()
    {
        _captureGetBuffer = null;
        _captureReleaseBuffer = null;
        _captureGetNextPacketSize = null;

        if (_captureClientPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Marshal.Release(_captureClientPtr);
        }
        catch
        {
            // 既に停止済み/解放済みの場合があるため握りつぶす。
        }
        finally
        {
            _captureClientPtr = IntPtr.Zero;
        }
    }

    private static IAudioClientNative? ResolveAudioClientInterface(IntPtr activatedInterfacePtr)
    {
        if (activatedInterfacePtr == IntPtr.Zero)
        {
            return null;
        }

        var audioClientPtr = IntPtr.Zero;

        try
        {
            var iidAudioClient = typeof(IAudioClientNative).GUID;
            var hr = Marshal.QueryInterface(activatedInterfacePtr, in iidAudioClient, out audioClientPtr);
            if (hr < 0 || audioClientPtr == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.GetObjectForIUnknown(audioClientPtr) as IAudioClientNative;
        }
        catch
        {
            // QueryInterface に失敗する環境では null を返して上位でフォールバックする。
            return null;
        }
        finally
        {
            if (audioClientPtr != IntPtr.Zero)
            {
                Marshal.Release(audioClientPtr);
            }

            Marshal.Release(activatedInterfacePtr);
        }
    }

    private static WaveFormat InitializeAudioClientWithFallbackFormats(IAudioClientNative audioClient, int sampleRate)
    {
        // 環境差異で失敗しやすいため、候補フォーマットを順に試して最初の成功を採用する。
        var preferredRate = sampleRate > 0 ? sampleRate : 48_000;
        var candidates = new[]
        {
            new WaveFormat(preferredRate, 16, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(preferredRate, 2),
            new WaveFormat(48_000, 16, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)
        };

        var lastHr = unchecked((int)0x88890008);
        foreach (var candidate in candidates)
        {
            var audioSessionGuid = Guid.Empty;
            var hr = audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback,
                20 * 10_000,
                0,
                candidate,
                ref audioSessionGuid);

            if (hr >= 0)
            {
                return candidate;
            }

            lastHr = hr;
        }

        Marshal.ThrowExceptionForHR(lastHr);
        throw new InvalidOperationException("Audio client initialization failed.");
    }

    private async Task MonitorTargetProcessAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_running)
                {
                    break;
                }

                if (!IsProcessAlive(_targetProcessId))
                {
                    _running = false;
                    TargetProcessExited?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求で待機が中断された場合は正常系として扱う。
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseComObject<T>(ref T? comObject)
        where T : class
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // 既に停止済み/解放済みの場合があるため握りつぶす。
        }
        finally
        {
            comObject = null;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct BLOB
    {
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public BLOB blob;
    }

    private enum AUDIOCLIENT_ACTIVATION_TYPE
    {
        AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
        AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    }

    private enum PROCESS_LOOPBACK_MODE
    {
        PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
        PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CaptureGetBufferDelegate(
        IntPtr thisPtr,
        out IntPtr data,
        out uint numFramesToRead,
        out AudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CaptureReleaseBufferDelegate(IntPtr thisPtr, uint numFramesRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CaptureGetNextPacketSizeDelegate(IntPtr thisPtr, out uint numFramesInNextPacket);

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClientNative
    {
        // COM vtable の順序を正確に合わせる必要があるため、
        // このサービスで未使用のメソッドも定義を省略しない。
        int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, [In, MarshalAs(UnmanagedType.LPStruct)] WaveFormat format, ref Guid audioSessionGuid);
        int GetBufferSize(out uint bufferSize);
        int GetStreamLatency(out long streamLatency);
        int GetCurrentPadding(out uint currentPadding);
        int IsFormatSupported(AudioClientShareMode shareMode, [In, MarshalAs(UnmanagedType.LPStruct)] WaveFormat format, out IntPtr closestMatchFormat);
        int GetMixFormat(out IntPtr deviceFormatPointer);
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid interfaceId, out IntPtr interfacePointer);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        int GetActivateResult(out int activateResult, out IntPtr activatedInterfacePtr);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<(int activateResult, IntPtr activatedInterfacePtr)> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            var hr = activateOperation.GetActivateResult(out var activateResult, out var activatedInterfacePtr);
            if (hr < 0)
            {
                _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("GetActivateResult failed.", hr));
            }
            else
            {
                _tcs.TrySetResult((activateResult, activatedInterfacePtr));
            }

            return 0;
        }

        public async Task<(int activateResult, IntPtr activatedInterfacePtr)> WaitAsync(CancellationToken cancellationToken)
        {
            await using var registration = cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
            return await _tcs.Task.ConfigureAwait(false);
        }
    }
}
