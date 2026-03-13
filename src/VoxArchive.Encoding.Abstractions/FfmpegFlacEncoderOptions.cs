namespace VoxArchive.Encoding.Abstractions;

public sealed record FfmpegFlacEncoderOptions(
    string OutputFilePath,
    int SampleRate,
    int Channels,
    int CompressionLevel,
    string ExecutablePath = "ffmpeg");
