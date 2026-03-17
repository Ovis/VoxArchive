using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public static class NAudioRuntimeSupport
{
    private const string MicCaptureType = "NAudio.CoreAudioApi.WasapiCapture, NAudio.Wasapi";
    private const string SpeakerCaptureType = "NAudio.Wave.WasapiLoopbackCapture, NAudio.Wasapi";

    public static bool IsAvailable()
    {
        return Type.GetType(MicCaptureType, throwOnError: false) is not null
            && Type.GetType(SpeakerCaptureType, throwOnError: false) is not null;
    }

    public static ISpeakerCaptureService CreateSpeakerCaptureService()
    {
        EnsureAvailable();
        return new NAudioSpeakerCaptureService();
    }

    public static IMicCaptureService CreateMicCaptureService()
    {
        EnsureAvailable();
        return new NAudioMicCaptureService();
    }

    private static void EnsureAvailable()
    {
        if (IsAvailable())
        {
            return;
        }

        throw new InvalidOperationException(
            "NAudio runtime is not available. Expected types not found: " +
            $"{MicCaptureType}, {SpeakerCaptureType}");
    }
}
