using System.Text;
using System.Text.Json;

namespace VoxArchive.Transcription;

/// <summary>
/// canonical transcription documentの永続化を担当する
/// </summary>
public sealed class TranscriptionDocumentStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// canonical documentを一時ファイル経由で保存する
    /// </summary>
    public async Task SaveAsync(
        string path,
        TranscriptionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != TranscriptionDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"保存できないcanonical schemaVersionです: {document.SchemaVersion}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// 現行canonical schemaのdocumentを読み込む
    /// </summary>
    public async Task<TranscriptionDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<TranscriptionDocument>(
            stream,
            SerializerOptions,
            cancellationToken)
            ?? throw new InvalidDataException("canonical transcription documentを読み込めませんでした。");

        // 未公開の旧canonical JSONを維持するmigrationは意図的に持たない。
        // schema不一致を曖昧に読み替えると新schemaの契約違反を隠すため、明示的に失敗させる。
        if (document.SchemaVersion != TranscriptionDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"未対応のcanonical schemaVersionです: {document.SchemaVersion}");
        }
        return document;
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Atomic writeの後始末はbest effortとし、確定済み正本へ影響させない。
        }
    }
}
