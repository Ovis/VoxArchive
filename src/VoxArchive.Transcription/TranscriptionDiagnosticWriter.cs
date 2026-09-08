using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VoxArchive.Transcription;

/// <summary>
/// 詳細文字起こし診断を既存ログディレクトリへJSONとして保存する
/// </summary>
public sealed class TranscriptionDiagnosticWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<TranscriptionDiagnosticWriter> _logger;
    private readonly string _logsDirectory;

    /// <summary>既定のVoxArchiveログディレクトリを使用するwriterを生成する</summary>
    public TranscriptionDiagnosticWriter(ILogger<TranscriptionDiagnosticWriter> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// 指定したログディレクトリを使用するwriterを生成する
    /// </summary>
    /// <param name="logger">診断JSON保存失敗を通常ログへ記録するlogger</param>
    /// <param name="logsDirectory">診断JSONを保存するディレクトリ。nullまたは空の場合は既定ログディレクトリを使用する</param>
    public TranscriptionDiagnosticWriter(
        ILogger<TranscriptionDiagnosticWriter> logger,
        string? logsDirectory)
    {
        _logger = logger;
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxArchive",
                "logs")
            : logsDirectory;
    }

    /// <summary>
    /// 診断JSONを保存する。保存失敗は通常ログへ警告として残し、文字起こしJobへ例外を返さない
    /// </summary>
    /// <returns>保存できた場合はファイルパス、失敗した場合はnull</returns>
    public async Task<string?> TryWriteAsync(
        string sourceRecordingPath,
        DateTimeOffset timestamp,
        TranscriptionDiagnosticDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordingPath);
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_logsDirectory);

            var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourceRecordingPath));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "transcription";

            // 呼び出し側がJob完了時刻として確定したDateTimeOffsetのoffsetをそのまま使う。
            // OSのローカルタイムゾーンへ変換すると、同じJobでも実行環境によって診断ファイル名が変わるため避ける。
            var timestampText = timestamp.ToString("yyyyMMdd-HHmmss");
            var path = ResolveCollisionFreePath(baseName, timestampText);

            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 診断は本処理の副産物なので、ディスク障害や権限エラーで文字起こし成功結果を失敗扱いにしない。
            _logger.LogWarning(ex, "Failed to write transcription diagnostic JSON. File={File}", Path.GetFileName(sourceRecordingPath));
            return null;
        }
    }

    private string ResolveCollisionFreePath(string baseName, string timestampText)
    {
        var stem = $"{baseName}-{timestampText}.transcription-diagnostic";
        var first = Path.Combine(_logsDirectory, stem + ".json");
        if (!File.Exists(first)) return first;

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(_logsDirectory, $"{stem}-{index}.json");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }
}
