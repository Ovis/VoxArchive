using NAudio.Wave;

namespace VoxArchive.Wpf;

internal static class NAudio3ReadCompatibilityExtensions
{
    public static int Read(this ISampleProvider provider, float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(buffer);
        return provider.Read(buffer.AsSpan(offset, count));
    }

    public static int Read(this AudioFileReader reader, float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(buffer);
        return ((ISampleProvider)reader).Read(buffer.AsSpan(offset, count));
    }
}
