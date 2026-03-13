using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public JsonSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<RecordingOptions> LoadRecordingOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new RecordingOptions();
        }

        await using var stream = File.OpenRead(_settingsPath);
        var options = await JsonSerializer.DeserializeAsync<RecordingOptions>(stream, SerializerOptions, cancellationToken);
        return options ?? new RecordingOptions();
    }

    public async Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, options, SerializerOptions, cancellationToken);
    }
}
