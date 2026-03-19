using NAudio.Wave;

namespace VoxArchive.Wpf;

public sealed class PlaybackRateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _readBuffer;

    private float[] _cache = Array.Empty<float>();
    private int _cachedFrames;
    private double _cursorFrames;
    private bool _sourceEnded;

    public PlaybackRateSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _readBuffer = new float[Math.Max(4096, _channels * 1024)];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float PlaybackRate { get; set; } = 1f;

    public int Read(float[] buffer, int offset, int count)
    {
        var targetFrames = count / _channels;
        if (targetFrames <= 0)
        {
            return 0;
        }

        var rate = Math.Clamp(PlaybackRate, 0.5f, 4f);
        var writtenFrames = 0;

        while (writtenFrames < targetFrames)
        {
            var needFrameIndex = (int)Math.Floor(_cursorFrames) + 1;
            if (!EnsureFrameAvailable(needFrameIndex))
            {
                break;
            }

            var i0 = Math.Clamp((int)Math.Floor(_cursorFrames), 0, _cachedFrames - 1);
            var i1 = Math.Clamp(i0 + 1, 0, _cachedFrames - 1);
            var frac = (float)(_cursorFrames - i0);

            var outBase = offset + (writtenFrames * _channels);
            var i0Base = i0 * _channels;
            var i1Base = i1 * _channels;

            for (var ch = 0; ch < _channels; ch++)
            {
                var a = _cache[i0Base + ch];
                var b = _cache[i1Base + ch];
                buffer[outBase + ch] = a + ((b - a) * frac);
            }

            writtenFrames++;
            _cursorFrames += rate;

            var dropFrames = Math.Max(0, (int)Math.Floor(_cursorFrames) - 1);
            if (dropFrames > 0)
            {
                DropFrames(dropFrames);
            }
        }

        return writtenFrames * _channels;
    }

    private bool EnsureFrameAvailable(int frameIndex)
    {
        while (_cachedFrames <= frameIndex)
        {
            if (_sourceEnded)
            {
                return _cachedFrames > 0;
            }

            var read = _source.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0)
            {
                _sourceEnded = true;
                break;
            }

            var readFrames = read / _channels;
            if (readFrames <= 0)
            {
                continue;
            }

            AppendFrames(_readBuffer, readFrames);
        }

        return _cachedFrames > 0 && frameIndex < _cachedFrames;
    }

    private void AppendFrames(float[] samples, int frames)
    {
        var neededSamples = (_cachedFrames + frames) * _channels;
        if (_cache.Length < neededSamples)
        {
            var newBuffer = new float[Math.Max(neededSamples, Math.Max(4096, _cache.Length * 2))];
            if (_cachedFrames > 0)
            {
                Array.Copy(_cache, 0, newBuffer, 0, _cachedFrames * _channels);
            }

            _cache = newBuffer;
        }

        Array.Copy(samples, 0, _cache, _cachedFrames * _channels, frames * _channels);
        _cachedFrames += frames;
    }

    private void DropFrames(int frames)
    {
        if (frames <= 0 || _cachedFrames <= 0)
        {
            return;
        }

        if (frames >= _cachedFrames)
        {
            _cachedFrames = 0;
            _cursorFrames = 0;
            return;
        }

        var remainingFrames = _cachedFrames - frames;
        Array.Copy(_cache, frames * _channels, _cache, 0, remainingFrames * _channels);
        _cachedFrames = remainingFrames;
        _cursorFrames -= frames;
        if (_cursorFrames < 0)
        {
            _cursorFrames = 0;
        }
    }
}
