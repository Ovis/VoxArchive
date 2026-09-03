using NAudio.Wave;
using SoundTouch;

namespace VoxArchive.Wpf;

public sealed class SoundTouchSampleProvider : ISampleProvider
{
    private const int SourceBufferFrames = 2048;

    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly float[] _sourceBuffer;
    private readonly Lock _gate = new();
    private bool _isFlushed;

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        var channels = Math.Max(1, source.WaveFormat.Channels);
        _sourceBuffer = new float[SourceBufferFrames * channels];

        _processor = new SoundTouchProcessor
        {
            SampleRate = source.WaveFormat.SampleRate,
            Channels = channels,
            Tempo = 1.0,
            Pitch = 1.0,
            Rate = 1.0
        };
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double Tempo
    {
        get => _processor.Tempo;
        set
        {
            lock (_gate)
            {
                _processor.Tempo = value;
            }
        }
    }

    public double Pitch
    {
        get => _processor.Pitch;
        set
        {
            lock (_gate)
            {
                _processor.Pitch = value;
            }
        }
    }

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            var channels = Math.Max(1, WaveFormat.Channels);
            var requestedFrames = buffer.Length / channels;
            if (requestedFrames <= 0)
            {
                return 0;
            }

            while (_processor.AvailableSamples < requestedFrames)
            {
                var sourceRead = _source.Read(_sourceBuffer.AsSpan());
                if (sourceRead <= 0)
                {
                    if (!_isFlushed)
                    {
                        _processor.Flush();
                        _isFlushed = true;
                    }

                    break;
                }

                var completeSamples = sourceRead - (sourceRead % channels);
                if (completeSamples <= 0)
                {
                    continue;
                }

                _processor.PutSamples(_sourceBuffer.AsSpan(0, completeSamples), completeSamples / channels);
            }

            var output = buffer[..(requestedFrames * channels)];
            output.Clear();
            var framesRead = _processor.ReceiveSamples(output, requestedFrames);
            return framesRead * channels;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _processor.Clear();
            _isFlushed = false;
        }
    }
}
