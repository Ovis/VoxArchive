using System.IO;
using System.Text.Json;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// canonical transcription documentの保存と読み込みを担当する
/// </summary>
/// <remarks>
/// 新規保存はschemaVersion=2だけを生成する。schemaVersionを持たない既存JSONはlegacy v1として読み取り、
/// メモリ上でv2相当へ正規化するが、読み込みだけで既存ファイルを書き換えない。
/// </remarks>
public sealed class TranscriptionDocumentStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 正本ドキュメントをUTF-8 JSONとして保存する
    /// </summary>
    public async Task SaveAsync(string path, TranscriptionDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != TranscriptionDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"保存できない文字起こしschemaVersionです: {document.SchemaVersion}");
        }

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// v2または既存のlegacy v1 JSONを読み込み、v2形式のドキュメントとして返す
    /// </summary>
    public async Task<TranscriptionDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (json.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement))
        {
            var schemaVersion = schemaVersionElement.GetInt32();
            if (schemaVersion != TranscriptionDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"未対応の文字起こしschemaVersionです: {schemaVersion}");
            }

            return JsonSerializer.Deserialize<TranscriptionDocument>(json.RootElement.GetRawText(), SerializerOptions)
                ?? throw new InvalidDataException("文字起こしドキュメントを読み込めませんでした。");
        }

        return ReadLegacyDocument(path, json.RootElement);
    }

    private static TranscriptionDocument ReadLegacyDocument(string path, JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("legacy文字起こしJSONにsegmentsがありません。");
        }

        var segments = new List<TranscriptionDocumentSegment>();
        foreach (var segment in segmentsElement.EnumerateArray())
        {
            segments.Add(new TranscriptionDocumentSegment
            {
                Start = segment.TryGetProperty("start", out var start) ? start.GetDouble() : 0,
                End = segment.TryGetProperty("end", out var end) ? end.GetDouble() : 0,
                Speaker = segment.TryGetProperty("speaker", out var speaker) && speaker.ValueKind != JsonValueKind.Null ? speaker.GetString() : null,
                Text = segment.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty
            });
        }

        // 旧形式はエンジン情報を保持していないが、VoxArchiveが従来生成していたJSONはWhisper由来である。
        // モデルは旧ファイル名の末尾から可能な範囲で復元し、不明な場合も推測で別モデルへ置き換えない。
        return new TranscriptionDocument
        {
            Source = new TranscriptionSource(Path.GetFileNameWithoutExtension(RemoveLegacyModelSuffix(path))),
            Transcription = new TranscriptionIdentity
            {
                Engine = "whisper",
                Model = ResolveLegacyModelId(path) ?? "unknown"
            },
            Runtime = new TranscriptionRuntime { Requested = "unknown", Actual = null },
            CreatedAt = File.GetLastWriteTimeUtc(path),
            Segments = segments
        };
    }

    private static string RemoveLegacyModelSuffix(string jsonPath)
    {
        var withoutExtension = Path.Combine(Path.GetDirectoryName(jsonPath) ?? string.Empty, Path.GetFileNameWithoutExtension(jsonPath));
        var model = ResolveLegacyModelId(jsonPath);
        return model is null ? withoutExtension : withoutExtension[..^(model.Length + 1)];
    }

    private static string? ResolveLegacyModelId(string jsonPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(jsonPath);
        foreach (var model in new[] { "large-v3", "medium", "small", "base", "tiny" })
        {
            if (fileName.EndsWith($"-{model}", StringComparison.OrdinalIgnoreCase)) return model;
        }
        return null;
    }
}
