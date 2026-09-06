using System.Text.Json;

namespace VoxArchive.Domain;

/// <summary>
/// Engine IDごとに永続化するopaque settings blobを保持する
/// </summary>
/// <remarks>
/// Domain/ApplicationはSettingsのJSON内容を解釈しない。schemaVersionとmigrationは各Engine側Settings Providerの責務とする。
/// </remarks>
public sealed record TranscriptionEngineSettings
{
    /// <summary>Engine固有settings schemaのversion</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Engine固有のopaque JSON object</summary>
    public JsonElement Settings { get; init; } = JsonSerializer.SerializeToElement(new { });
}
