using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

public sealed class JsonSettingsService(string settingsPath) : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<RecordingOptions> LoadRecordingOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return new RecordingOptions();
        }

        await using var stream = File.OpenRead(settingsPath);
        var options = await JsonSerializer.DeserializeAsync<RecordingOptions>(stream, SerializerOptions, cancellationToken);
        return options ?? new RecordingOptions();
    }

    public async Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, options, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(settingsPath))
        {
            File.Replace(tempPath, settingsPath, null);
        }
        else
        {
            File.Move(tempPath, settingsPath);
        }
    }
}
