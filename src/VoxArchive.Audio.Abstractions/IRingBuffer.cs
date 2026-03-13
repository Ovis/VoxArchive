namespace VoxArchive.Audio.Abstractions;

public interface IRingBuffer
{
    int Capacity { get; }
    int Count { get; }

    int Write(ReadOnlySpan<float> source);
    int Read(Span<float> destination);
    int ReadWithZeroPadding(Span<float> destination);
    void Clear();
}
