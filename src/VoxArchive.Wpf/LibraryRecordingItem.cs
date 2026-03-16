using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoxArchive.Wpf;

public sealed class LibraryRecordingItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public long DurationMilliseconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public string DurationText => TimeSpan.FromMilliseconds(DurationMilliseconds).ToString(@"hh\:mm\:ss");
    public string SizeText => $"{FileSizeBytes / 1024d / 1024d:F2} MB";
    public string UpdatedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
}

