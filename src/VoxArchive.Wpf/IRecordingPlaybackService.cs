using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public interface IRecordingPlaybackService : IDisposable
{
    event EventHandler? PlaybackStopped;

    bool IsLoaded { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double PlaybackSpeed { get; }

    void Load(string filePath);
    void LoadEdited(string filePath, AudioEditState state, AudioRenderChannelMode channelMode);
    void Play();
    void Pause();
    void Stop();
    void Unload();
    void Seek(TimeSpan position);
    void SetGains(double leftDb, double rightDb);
    void SetMixToMono(bool enabled);
    void SetPlaybackSpeed(double speed);
}
