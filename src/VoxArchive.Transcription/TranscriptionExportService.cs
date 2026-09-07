using System.Text;

namespace VoxArchive.Transcription;

/// <summary>
/// canonical documentからTXT/SRT/VTTの派生物を生成する
/// </summary>
public sealed class TranscriptionExportService
{
    /// <summary>
    /// 指定された派生形式をcanonical JSONと同じbasenameで生成する
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteDerivedAsync(
        string documentPath,
        TranscriptionDocument document,
        TranscriptionArtifactFormats formats,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(document);
        var basePath = Path.Combine(
            Path.GetDirectoryName(documentPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(documentPath));
        var generated = new List<string>(3);

        foreach (var (format, extension) in EnumerateFormats(formats))
        {
            var path = basePath + extension;
            var content = format switch
            {
                TranscriptionArtifactFormats.Txt => BuildTxt(document.Segments),
                TranscriptionArtifactFormats.Srt => BuildSrt(document.Segments),
                TranscriptionArtifactFormats.Vtt => BuildVtt(document.Segments),
                _ => throw new InvalidOperationException($"未対応の派生形式です: {format}")
            };
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
            generated.Add(path);
        }

        return generated;
    }

    private static IEnumerable<(TranscriptionArtifactFormats Format, string Extension)> EnumerateFormats(
        TranscriptionArtifactFormats formats)
    {
        if (formats.HasFlag(TranscriptionArtifactFormats.Txt)) yield return (TranscriptionArtifactFormats.Txt, ".txt");
        if (formats.HasFlag(TranscriptionArtifactFormats.Srt)) yield return (TranscriptionArtifactFormats.Srt, ".srt");
        if (formats.HasFlag(TranscriptionArtifactFormats.Vtt)) yield return (TranscriptionArtifactFormats.Vtt, ".vtt");
    }

    private static string BuildTxt(IReadOnlyList<TranscriptionDocumentSegment> segments)
        => string.Join(Environment.NewLine, segments.Select(FormatText).Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string BuildSrt(IReadOnlyList<TranscriptionDocumentSegment> segments)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            builder.AppendLine((i + 1).ToString());
            builder.AppendLine($"{FormatTimestamp(segment.Start, ',')} --> {FormatTimestamp(segment.End, ',')}");
            builder.AppendLine(FormatText(segment));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string BuildVtt(IReadOnlyList<TranscriptionDocumentSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT");
        builder.AppendLine();
        foreach (var segment in segments)
        {
            builder.AppendLine($"{FormatTimestamp(segment.Start, '.')} --> {FormatTimestamp(segment.End, '.')}");
            builder.AppendLine(FormatText(segment));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatText(TranscriptionDocumentSegment segment)
    {
        var text = segment.Text.Trim();
        return string.IsNullOrWhiteSpace(segment.Speaker) ? text : $"[{segment.Speaker}] {text}";
    }

    private static string FormatTimestamp(double seconds, char separator)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
    }
}
