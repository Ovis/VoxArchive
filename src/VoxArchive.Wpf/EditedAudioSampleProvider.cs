using System.IO;
using NAudio.Wave;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 編集後音声を再生要求に応じて読み出す。長時間録音の一時WAV生成を不要にする。
/// </summary>
public sealed class EditedAudioSampleProvider : ISampleProvider
{
    private const int ReadBufferFrames = 4096;

    private readonly AudioFileReader _reader;
    private readonly AudioEditState _state;
    private readonly AudioRenderChannelMode _channelMode;
    private readonly Segment[] _segments;
    private readonly float[] _input;
    private readonly object _gate = new();
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private readonly int _sampleRate;
    private readonly long _frameCount;
    private long _positionFrames;
    private long _readerFrame = -1;
    private int _segmentIndex;

    public EditedAudioSampleProvider(AudioFileReader reader, AudioEditState state, AudioRenderChannelMode channelMode)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _channelMode = channelMode;
        _inputChannels = reader.WaveFormat.Channels;
        if (_inputChannels != state.ChannelCount || _inputChannels is < 1 or > 2)
            throw new InvalidOperationException("編集状態のチャンネル数が入力ファイルと一致しません。");

        _sampleRate = reader.WaveFormat.SampleRate;
        _outputChannels = AudioFrameProcessor.GetOutputChannelCount(_inputChannels, channelMode);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _outputChannels);
        _input = new float[ReadBufferFrames * _inputChannels];

        var plan = AudioRenderPlan.Create(state, _sampleRate);
        var segments = new List<Segment>();
        long outputCursor = 0;
        for (var index = 0; index < plan.KeepRanges.Count; index++)
        {
            var range = plan.KeepRanges[index];
            var incoming = index == 0 ? 0 : plan.Junctions[index - 1].CrossfadeFrameCount;
            var outgoing = index < plan.Junctions.Count ? plan.Junctions[index].CrossfadeFrameCount : 0;
            var bodyStart = range.StartFrame + incoming;
            var bodyLength = Math.Max(0, range.EndFrameExclusive - outgoing - bodyStart);
            if (bodyLength > 0)
            {
                segments.Add(new Segment(outputCursor, bodyLength, bodyStart));
                outputCursor += bodyLength;
            }

            if (outgoing > 0)
            {
                segments.Add(new Segment(outputCursor, outgoing, range.EndFrameExclusive - outgoing,
                    plan.KeepRanges[index + 1].StartFrame));
                outputCursor += outgoing;
            }
        }

        _segments = segments.ToArray();
        _frameCount = outputCursor;
        Duration = TimeSpan.FromSeconds((double)_frameCount / _sampleRate);
    }

    public WaveFormat WaveFormat { get; }
    public TimeSpan Duration { get; }

    public TimeSpan Position
    {
        get
        {
            lock (_gate) return TimeSpan.FromSeconds((double)_positionFrames / _sampleRate);
        }
    }

    public void Seek(TimeSpan position)
    {
        var frame = position <= TimeSpan.Zero ? 0
            : position >= Duration ? _frameCount
            : AudioRenderPlan.TimeToFrameIndex(position, _sampleRate);
        lock (_gate)
        {
            _positionFrames = Math.Clamp(frame, 0, _frameCount);
            _segmentIndex = 0;
            while (_segmentIndex < _segments.Length && _positionFrames >= _segments[_segmentIndex].EndFrame)
                _segmentIndex++;
            _readerFrame = -1;
        }
    }

    public int Read(Span<float> buffer)
    {
        var requestedFrames = buffer.Length / _outputChannels;
        if (requestedFrames == 0) return 0;

        lock (_gate)
        {
            var writtenFrames = 0;
            while (writtenFrames < requestedFrames && _segmentIndex < _segments.Length)
            {
                var segment = _segments[_segmentIndex];
                var segmentOffset = _positionFrames - segment.OutputStart;
                var frames = (int)Math.Min(Math.Min(segment.FrameCount - segmentOffset,
                    requestedFrames - writtenFrames), ReadBufferFrames);
                var destination = buffer.Slice(writtenFrames * _outputChannels, frames * _outputChannels);
                if (segment.RightSourceStart is { } rightStart)
                {
                    var mixed = segment.CrossfadeSamples ??= BuildCrossfade(segment.SourceStart, rightStart, (int)segment.FrameCount);
                    mixed.AsSpan((int)segmentOffset * _outputChannels, destination.Length).CopyTo(destination);
                }
                else
                {
                    ReadBody(segment.SourceStart + segmentOffset, frames, destination);
                }

                writtenFrames += frames;
                _positionFrames += frames;
                if (_positionFrames >= segment.EndFrame) _segmentIndex++;
            }
            return writtenFrames * _outputChannels;
        }
    }

    private void ReadBody(long sourceFrame, int frameCount, Span<float> output)
    {
        SeekReader(sourceFrame);
        ReadExactly(_input, frameCount * _inputChannels);
        for (var frame = 0; frame < frameCount; frame++)
        {
            AudioFrameProcessor.ProcessFrame(
                _input.AsSpan(frame * _inputChannels, _inputChannels),
                output.Slice(frame * _outputChannels, _outputChannels),
                _state.Channels, _channelMode);
        }
    }

    private float[] BuildCrossfade(long leftStart, long rightStart, int frameCount)
    {
        var left = ReadProcessed(leftStart, frameCount);
        var right = ReadProcessed(rightStart, frameCount);
        var mixed = new float[frameCount * _outputChannels];
        AudioCrossfadeMixer.Mix(left, right, mixed, _outputChannels);
        return mixed;
    }

    private float[] ReadProcessed(long sourceFrame, int frameCount)
    {
        var input = new float[frameCount * _inputChannels];
        var output = new float[frameCount * _outputChannels];
        SeekReader(sourceFrame);
        ReadExactly(input, input.Length);
        for (var frame = 0; frame < frameCount; frame++)
        {
            AudioFrameProcessor.ProcessFrame(
                input.AsSpan(frame * _inputChannels, _inputChannels),
                output.AsSpan(frame * _outputChannels, _outputChannels),
                _state.Channels, _channelMode);
        }
        return output;
    }

    private void SeekReader(long frame)
    {
        if (_readerFrame == frame) return;
        _reader.Position = Math.Min(_reader.Length, checked(frame * _reader.WaveFormat.BlockAlign));
        _readerFrame = frame;
    }

    private void ReadExactly(float[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = _reader.Read(buffer, total, count - total);
            if (read <= 0)
                throw new EndOfStreamException("入力音声が編集中に短くなりました。ほかのアプリによるファイル更新を確認してください。");
            total += read;
        }
        _readerFrame += count / _inputChannels;
    }

    private sealed class Segment(long outputStart, long frameCount, long sourceStart, long? rightSourceStart = null)
    {
        public long OutputStart { get; } = outputStart;
        public long FrameCount { get; } = frameCount;
        public long EndFrame => OutputStart + FrameCount;
        public long SourceStart { get; } = sourceStart;
        public long? RightSourceStart { get; } = rightSourceStart;
        public float[]? CrossfadeSamples { get; set; }
    }
}
