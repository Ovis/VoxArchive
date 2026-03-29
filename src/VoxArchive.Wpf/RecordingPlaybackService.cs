using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundTouch.Net.NAudioSupport;

namespace VoxArchive.Wpf;

public sealed class RecordingPlaybackService : IRecordingPlaybackService
{
    private const double MinPlaybackSpeed = 0.5d;
    private const double MaxPlaybackSpeed = 4.0d;
    private const int SharedOutputLatencyMilliseconds = 100;

    private WasapiOut? _output;
    private AudioFileReader? _reader;
    private StereoGainSampleProvider? _gainProvider;
    private SoundTouchWaveProvider? _timeStretchProvider;
    private double _playbackSpeed = 1.0;

    public event EventHandler? PlaybackStopped;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;
    public double PlaybackSpeed => _playbackSpeed;

    public void Load(string filePath)
    {
        Stop();
        DisposeCore();

        _reader = new AudioFileReader(filePath);
        _gainProvider = new StereoGainSampleProvider(_reader);
        _timeStretchProvider = new SoundTouchWaveProvider(_gainProvider.ToWaveProvider())
        {
            // Tempo を変更するとピッチを維持したまま再生速度だけを変更できる。
            Tempo = (float)_playbackSpeed,
            Pitch = 1.0f
        };

        _output = new WasapiOut(AudioClientShareMode.Shared, SharedOutputLatencyMilliseconds);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_timeStretchProvider);
    }

    public void Play()
    {
        _output?.Play();
    }

    public void Pause()
    {
        _output?.Pause();
    }

    public void Stop()
    {
        _output?.Stop();
        if (_reader is not null)
        {
            _reader.CurrentTime = TimeSpan.Zero;
        }

        _timeStretchProvider?.Clear();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null)
        {
            return;
        }

        var targetSeconds = Math.Clamp(position.TotalSeconds, 0d, _reader.TotalTime.TotalSeconds);
        _reader.CurrentTime = TimeSpan.FromSeconds(targetSeconds);
        _timeStretchProvider?.Clear();
    }

    public void SetGains(double leftDb, double rightDb)
    {
        if (_gainProvider is null)
        {
            return;
        }

        _gainProvider.LeftGain = DbToLinear(leftDb);
        _gainProvider.RightGain = DbToLinear(rightDb);
    }

    public void SetMixToMono(bool enabled)
    {
        if (_gainProvider is null)
        {
            return;
        }

        _gainProvider.MixToMono = enabled;
    }

    public void SetPlaybackSpeed(double speed)
    {
        _playbackSpeed = Math.Clamp(speed, MinPlaybackSpeed, MaxPlaybackSpeed);
        if (_timeStretchProvider is not null)
        {
            _timeStretchProvider.Tempo = (float)_playbackSpeed;
            _timeStretchProvider.Pitch = 1.0f;
        }
    }

    private static float DbToLinear(double db)
    {
        return (float)Math.Pow(10d, db / 20d);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeCore()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
        _gainProvider = null;
        _timeStretchProvider = null;
    }

    public void Dispose()
    {
        DisposeCore();
    }
}
