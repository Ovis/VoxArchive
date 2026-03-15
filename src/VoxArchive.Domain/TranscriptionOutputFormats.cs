using System;

namespace VoxArchive.Domain;

[Flags]
public enum TranscriptionOutputFormats
{
    None = 0,
    Txt = 1 << 0,
    Srt = 1 << 1,
    Vtt = 1 << 2,
    Json = 1 << 3
}
