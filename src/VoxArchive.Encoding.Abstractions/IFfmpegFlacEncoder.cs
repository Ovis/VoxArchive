namespace VoxArchive.Encoding.Abstractions;

public interface IFfmpegFlacEncoder : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync(FfmpegFlacEncoderOptions options, CancellationToken cancellationToken = default);
    Task WriteAsync(ReadOnlyMemory<byte> pcm16StereoFrame, CancellationToken cancellationToken = default);
    Task<FfmpegStopResult> StopAsync(CancellationToken cancellationToken = default);
}
