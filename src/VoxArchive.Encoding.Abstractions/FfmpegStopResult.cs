namespace VoxArchive.Encoding.Abstractions;

public sealed record FfmpegStopResult(
    int ExitCode,
    bool IsSuccess,
    string StandardError);
