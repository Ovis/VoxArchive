using System.IO;
using System.Text.Json;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 録音ファイルに対応する文字起こし結果を、正本JSONのメタデータだけで列挙する
/// </summary>
/// <remarks>
/// ライブラリ一覧では全文セグメントを読む必要がないため、まず軽量なメタデータだけを走査する。
/// 選択された結果の本文は後続の詳細表示で <see cref="TranscriptionDocumentStore"/> から読み込む。
/// </remarks>
public sealed class TranscriptionResultDiscoveryService
{
    /// <summary>
    /// 指定録音に対応するcanonical/legacy JSONを列挙する
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionResultMetadata>> DiscoverAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var recordingName = Path.GetFileNameWithoutExtension(audioFilePath);
        if (!Directory.Exists(directory)) return [];

        var results = new List<TranscriptionResultMetadata>();
        foreach (var path in Directory.EnumerateFiles(directory, $"{recordingName}-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                results.Add(ReadMetadata(path, json.RootElement));
            }
            catch (JsonException)
            {
                // 手編集などで壊れたJSONがあってもライブラリ全体を開けなくなるのは避ける。
                // Corrupted状態の表示はモデル管理と合わせて後続で導入するため、現段階では発見対象から除外する。
            }
        }

        return results.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    private static TranscriptionResultMetadata ReadMetadata(string path, JsonElement root)
    {
        if (root.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            if (schemaVersion.GetInt32() != TranscriptionDocument.CurrentSchemaVersion)
            {
                throw new JsonException($"未対応のschemaVersionです: {schemaVersion.GetInt32()}");
            }

            var transcription = root.GetProperty("transcription");
            return new TranscriptionResultMetadata(
                path,
                transcription.GetProperty("engine").GetString() ?? "unknown",
                transcription.GetProperty("model").GetString() ?? "unknown",
                root.TryGetProperty("createdAt", out var createdAt) && createdAt.TryGetDateTimeOffset(out var parsedCreatedAt)
                    ? parsedCreatedAt
                    : File.GetLastWriteTimeUtc(path),
                IsLegacy: false);
        }

        return new TranscriptionResultMetadata(
            path,
            "whisper",
            ResolveLegacyModelId(path) ?? "unknown",
            File.GetLastWriteTimeUtc(path),
            IsLegacy: true);
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

/// <summary>
/// ライブラリ一覧で利用する文字起こし結果の軽量メタデータを表す
/// </summary>
public sealed record TranscriptionResultMetadata(
    string DocumentPath,
    string EngineId,
    string ModelId,
    DateTimeOffset CreatedAt,
    bool IsLegacy);
