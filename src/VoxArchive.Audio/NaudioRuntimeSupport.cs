using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public static class NaudioRuntimeSupport
{
    public static bool IsAvailable()
    {
        return Type.GetType("NAudio.Wave.WasapiCapture, NAudio", throwOnError: false) is not null
            && Type.GetType("NAudio.Wave.WasapiLoopbackCapture, NAudio", throwOnError: false) is not null;
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
}
