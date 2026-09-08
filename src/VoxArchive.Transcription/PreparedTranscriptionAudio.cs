using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Common側が生成した一時音声の所有権とライフサイクルを管理する
/// </summary>
internal sealed class PreparedTranscriptionAudio(
    string filePath,
    TranscriptionAudioRequirements format,
    TimeSpan duration,
    long sampleCount) : IPreparedTranscriptionAudio
{
    private int _disposed;

    /// <inheritdoc />
    public TranscriptionAudioRequirements Format { get; } = format;

    /// <inheritdoc />
    public TimeSpan Duration { get; } = duration;

    /// <inheritdoc />
    public long SampleCount { get; } = sampleCount;

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // 一時ファイル削除失敗だけで文字起こし結果を失敗扱いにしない。
                // GUID名で生成するため後続Jobとの衝突は発生せず、OSの一時領域掃除へ委ねられる。
            }
        }

        return ValueTask.CompletedTask;
    }
}
