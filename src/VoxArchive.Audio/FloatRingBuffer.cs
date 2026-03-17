using VoxArchive.Audio.Abstractions;

namespace VoxArchive.Audio;

public sealed class FloatRingBuffer : IRingBuffer
{
    private readonly Lock _sync = new();
    private readonly float[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    public FloatRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public int Write(ReadOnlySpan<float> source)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        lock (_sync)
        {
            var writable = Math.Min(source.Length, Capacity - _count);
            if (writable <= 0)
            {
                return 0;
            }

            var first = Math.Min(writable, Capacity - _tail);
            source.Slice(0, first).CopyTo(_buffer.AsSpan(_tail, first));

            var remaining = writable - first;
            if (remaining > 0)
            {
                source.Slice(first, remaining).CopyTo(_buffer.AsSpan(0, remaining));
            }

            _tail = (_tail + writable) % Capacity;
            _count += writable;
            return writable;
        }
    }

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_sync)
        {
            var readable = Math.Min(destination.Length, _count);
            if (readable <= 0)
            {
                return 0;
            }

            ReadCore(destination, readable);
            return readable;
        }
    }

    public int ReadWithZeroPadding(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        var read = Read(destination);
        if (read < destination.Length)
        {
            destination.Slice(read).Clear();
        }

        return destination.Length;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }

    private void ReadCore(Span<float> destination, int readable)
    {
        var first = Math.Min(readable, Capacity - _head);
        _buffer.AsSpan(_head, first).CopyTo(destination.Slice(0, first));

        var remaining = readable - first;
        if (remaining > 0)
        {
            _buffer.AsSpan(0, remaining).CopyTo(destination.Slice(first, remaining));
        }

        _head = (_head + readable) % Capacity;
        _count -= readable;
    }
}
