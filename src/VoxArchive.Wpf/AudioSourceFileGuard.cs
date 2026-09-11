using System.IO;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorを開いた時点の元ファイル状態を保持し、削除・差し替えを重要操作前に検出する。
/// </summary>
public sealed class AudioSourceFileGuard
{
    private readonly string _fullPath;
    private readonly long _length;
    private readonly DateTime _lastWriteUtc;

    public AudioSourceFileGuard(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(_fullPath);
        if (!info.Exists) throw new FileNotFoundException("元音声ファイルが見つかりません。", _fullPath);
        _length = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
    }

    public string FilePath => _fullPath;

    /// <summary>
    /// 元ファイルがEditor起動後に削除または更新されていないことを確認する。
    /// </summary>
    public void ValidateUnchanged()
    {
        var info = new FileInfo(_fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("元音声ファイルが削除されています。Audio Editorを閉じてライブラリを更新してください。", _fullPath);
        }

        if (info.Length != _length || info.LastWriteTimeUtc != _lastWriteUtc)
        {
            throw new InvalidOperationException("元音声ファイルがAudio Editorを開いた後に変更されています。安全のため、Editorを閉じて開き直してください。");
        }
    }
}
