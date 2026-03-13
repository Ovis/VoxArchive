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
        return IsAvailable() ? new NaudioSpeakerCaptureService() : new SpeakerCaptureService();
    }

    public static IMicCaptureService CreateMicCaptureService()
    {
        return IsAvailable() ? new NaudioMicCaptureService() : new MicCaptureService();
    }
}
