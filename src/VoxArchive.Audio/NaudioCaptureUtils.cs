using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

internal static class NaudioCaptureUtils
{
    public static object CreateCapture(string captureTypeName, string? deviceId)
    {
        var captureType = Type.GetType(captureTypeName, throwOnError: true)!;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var mmDevice = TryGetDevice(deviceId);
            if (mmDevice is not null)
            {
                var ctorWithDevice = captureType.GetConstructor(new[] { mmDevice.GetType() });
                if (ctorWithDevice is not null)
                {
                    return ctorWithDevice.Invoke(new[] { mmDevice });
                }
            }
        }

        var defaultCtor = captureType.GetConstructor(Type.EmptyTypes);
        if (defaultCtor is null)
        {
            throw new InvalidOperationException($"No default constructor found for {captureTypeName}.");
        }

        return defaultCtor.Invoke(Array.Empty<object>());
    }

    public static int ResolveSampleRate(object capture)
    {
        var waveFormat = capture.GetType().GetProperty("WaveFormat")?.GetValue(capture)
            ?? throw new InvalidOperationException("WaveFormat not found.");
        return (int)(waveFormat.GetType().GetProperty("SampleRate")?.GetValue(waveFormat)
            ?? throw new InvalidOperationException("WaveFormat.SampleRate not found."));
    }

    public static (int Channels, int BitsPerSample, bool IsFloat) ResolveFormat(object capture)
    {
        var waveFormat = capture.GetType().GetProperty("WaveFormat")?.GetValue(capture)
            ?? throw new InvalidOperationException("WaveFormat not found.");
        var waveFormatType = waveFormat.GetType();

        var channels = (int)(waveFormatType.GetProperty("Channels")?.GetValue(waveFormat) ?? 1);
        var bitsPerSample = (int)(waveFormatType.GetProperty("BitsPerSample")?.GetValue(waveFormat) ?? 16);

        var encodingObj = waveFormatType.GetProperty("Encoding")?.GetValue(waveFormat);
        var encodingName = encodingObj?.ToString() ?? string.Empty;
        var isFloat = encodingName.Contains("Float", StringComparison.OrdinalIgnoreCase)
            || encodingName.Contains("IeeeFloat", StringComparison.OrdinalIgnoreCase);

        return (channels, bitsPerSample, isFloat);
    }

    public static float[] ToMonoFloat(byte[] buffer, int bytesRecorded, int channels, int bitsPerSample, bool isFloat)
    {
        if (bytesRecorded <= 0 || channels <= 0)
        {
            return Array.Empty<float>();
        }

        var bytesPerSample = Math.Max(1, bitsPerSample / 8);
        var frameSize = bytesPerSample * channels;
        if (frameSize <= 0)
        {
            return Array.Empty<float>();
        }

        var frameCount = bytesRecorded / frameSize;
        var output = new float[frameCount];

        for (var i = 0; i < frameCount; i++)
        {
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
            {
                var idx = (i * frameSize) + (ch * bytesPerSample);
                sum += ReadSample(buffer, idx, bitsPerSample, isFloat);
            }

            output[i] = sum / channels;
        }

        return output;
    }

    public static void StartRecording(object capture)
    {
        capture.GetType().GetMethod("StartRecording")?.Invoke(capture, null);
    }

    public static void StopRecording(object capture)
    {
        capture.GetType().GetMethod("StopRecording")?.Invoke(capture, null);
    }

    public static void DisposeCapture(object capture)
    {
        if (capture is IDisposable d)
        {
            d.Dispose();
        }
    }

    private static object? TryGetDevice(string deviceId)
    {
        var enumeratorType = Type.GetType("NAudio.CoreAudioApi.MMDeviceEnumerator, NAudio.Core", throwOnError: false);
        if (enumeratorType is null)
        {
            return null;
        }

        var enumerator = Activator.CreateInstance(enumeratorType);
        if (enumerator is null)
        {
            return null;
        }

        try
        {
            var getDevice = enumeratorType.GetMethod("GetDevice", new[] { typeof(string) });
            return getDevice?.Invoke(enumerator, new object[] { deviceId });
        }
        finally
        {
            if (enumerator is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    private static float ReadSample(byte[] buffer, int index, int bitsPerSample, bool isFloat)
    {
        if (bitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, index) / 32768f;
        }

        if (bitsPerSample == 24)
        {
            var value = buffer[index] | (buffer[index + 1] << 8) | (buffer[index + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xFF000000);
            }

            return value / 8388608f;
        }

        if (bitsPerSample == 32)
        {
            return isFloat
                ? BitConverter.ToSingle(buffer, index)
                : BitConverter.ToInt32(buffer, index) / 2147483648f;
        }

        return 0f;
    }
}

