using System.IO;
using System.Text;
using System.Text.Json;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こし結果からTXT/SRT/VTT/JSONの既存出力を生成する
/// </summary>
public sealed class TranscriptionExportService
{
    private static readonly (TranscriptionOutputFormats Format, string Extension)[] OutputFormatMap =
    [
        (TranscriptionOutputFormats.Txt, ".txt"),
        (TranscriptionOutputFormats.Srt, ".srt"),
        (TranscriptionOutputFormats.Vtt, ".vtt"),
        (TranscriptionOutputFormats.Json, ".json")
    ];

    /// <summary>
    /// 指定された形式の出力ファイルを録音ファイルと同じディレクトリへ生成する
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteAsync(string audioFilePath, TranscriptionModel model, TranscriptionOutputFormats formats, IReadOnlyList<TranscribedSegment> segments, CancellationToken cancellationToken)
    {
        var basePath = BuildOutputBasePath(audioFilePath, model);
        var generated = new List<string>(4);
        foreach (var (format, extension) in EnumerateRequestedOutputFormats(formats))
        {
            var path = basePath + extension;
            var text = format switch
            {
                TranscriptionOutputFormats.Txt => string.Join(Environment.NewLine, segments.Select(FormatSegmentText).Where(x => !string.IsNullOrWhiteSpace(x))),
                TranscriptionOutputFormats.Srt => BuildSrtContent(segments),
                TranscriptionOutputFormats.Vtt => BuildVttContent(segments),
                TranscriptionOutputFormats.Json => BuildJsonContent(segments),
                _ => throw new InvalidOperationException($"未対応の文字起こし出力形式です: {format}")
            };
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8, cancellationToken);
            generated.Add(path);
        }
        return generated;
    }

    /// <summary>
    /// 現在の命名規則に従って、指定形式で生成されるファイルパスを列挙する
    /// </summary>
    public IReadOnlyList<string> BuildOutputPaths(string audioFilePath, TranscriptionModel model, TranscriptionOutputFormats formats)
    {
        var basePath = BuildOutputBasePath(audioFilePath, model);
        return EnumerateRequestedOutputFormats(formats).Select(x => basePath + x.Extension).ToArray();
    }

    private static IEnumerable<(TranscriptionOutputFormats Format, string Extension)> EnumerateRequestedOutputFormats(TranscriptionOutputFormats formats)
    {
        foreach (var item in OutputFormatMap) if (formats.HasFlag(item.Format)) yield return item;
    }

    private static string BuildSrtContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{FormatTimestamp(segment.Start, ',')} --> {FormatTimestamp(segment.End, ',')}");
            sb.AppendLine(FormatSegmentText(segment));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildVttContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        foreach (var segment in segments)
        {
            sb.AppendLine($"{FormatTimestamp(segment.Start, '.')} --> {FormatTimestamp(segment.End, '.')}");
            sb.AppendLine(FormatSegmentText(segment));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildJsonContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var payload = new { segments = segments.Select(x => new { start = x.Start.TotalSeconds, end = x.End.TotalSeconds, speaker = x.SpeakerLabel, text = x.Text }) };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatSegmentText(TranscribedSegment segment)
    {
        var text = segment.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return string.IsNullOrWhiteSpace(segment.SpeakerLabel) ? text : $"[{segment.SpeakerLabel}] {text}";
    }

    private static string BuildOutputBasePath(string audioFilePath, TranscriptionModel model)
    {
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(audioFilePath);
        var modelName = model switch
        {
            TranscriptionModel.Tiny => "tiny",
            TranscriptionModel.Base => "base",
            TranscriptionModel.Small => "small",
            TranscriptionModel.Medium => "medium",
            TranscriptionModel.LargeV3 => "large-v3",
            _ => model.ToString().ToLowerInvariant()
        };
        return Path.Combine(directory, $"{fileName}-{modelName}");
    }

    private static string FormatTimestamp(TimeSpan time, char separator) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
}
