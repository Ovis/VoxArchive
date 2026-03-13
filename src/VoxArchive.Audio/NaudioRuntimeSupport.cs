using System.Reflection;
using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public static class NaudioRuntimeSupport
{
    public static bool IsAvailable()
    {
        return TryGetNaudioAssembly() is not null;
    }

    public static ISpeakerCaptureService CreateSpeakerCaptureService()
    {
        EnsureAvailable();
        return new NaudioSpeakerCaptureService();
    }

    public static IMicCaptureService CreateMicCaptureService()
    {
        EnsureAvailable();
        return new NaudioMicCaptureService();
    }

    private static void EnsureAvailable()
    {
        if (IsAvailable())
        {
            return;
        }

        throw new InvalidOperationException(
            "NAudio runtime is not available. Ensure the NAudio package is restored and deployed.");
    }

    private static Assembly? TryGetNaudioAssembly()
    {
        try
        {
            var assembly = Assembly.Load("NAudio");
            var hasCapture = assembly.GetType("NAudio.Wave.WasapiCapture", throwOnError: false) is not null;
            var hasLoopback = assembly.GetType("NAudio.Wave.WasapiLoopbackCapture", throwOnError: false) is not null;
            return hasCapture && hasLoopback ? assembly : null;
        }
        catch
        {
            return null;
        }
    }
}
