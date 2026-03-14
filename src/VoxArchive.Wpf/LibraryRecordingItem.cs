namespace VoxArchive.Wpf;

public sealed class LibraryRecordingItem
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public long DurationMilliseconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public bool IsHidden { get; init; }

    public string DurationText => TimeSpan.FromMilliseconds(DurationMilliseconds).ToString(@"hh\:mm\:ss");
    public string SizeText => $"{FileSizeBytes / 1024d / 1024d:F2} MB";
    public string UpdatedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string StatusText => IsHidden ? "除外" : "通常";
}
