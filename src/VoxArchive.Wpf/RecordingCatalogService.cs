using Microsoft.Data.Sqlite;

namespace VoxArchive.Wpf;

public sealed class RecordingCatalogService
{
    private readonly string _dbPath;

    public RecordingCatalogService(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task<IReadOnlyList<LibraryRecordingItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        await using var connection = OpenConnection();
        return await GetAllCoreAsync(connection, cancellationToken);
    }

    public async Task AddOrUpdateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        var (title, durationMs, sampleRate, channels) = ReadMetadata(filePath, fileInfo.Name);

        await using var connection = OpenConnection();
        await UpsertAsync(
            connection,
            filePath,
            fileInfo.Name,
            title,
            durationMs,
            sampleRate,
            channels,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            cancellationToken);
    }

    public async Task UpdateTitleAsync(string filePath, string title, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Title = title;
            tagFile.Save();
        }

        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE recordings
SET title = $title,
    updated_utc = $updated
WHERE file_path = $path;";
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$path", filePath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> RenameAsync(string filePath, string newName, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        var targetName = newName.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new InvalidOperationException("新しいファイル名が空です。");
        }

        if (!targetName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
        {
            targetName += ".flac";
        }

        var directory = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("フォルダが取得できません。");
        var newPath = Path.Combine(directory, targetName);

        if (string.Equals(filePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        if (File.Exists(newPath))
        {
            throw new IOException("同名ファイルが既に存在します。");
        }

        File.Move(filePath, newPath);

        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE recordings
SET file_path = $newPath,
    file_name = $newName,
    updated_utc = $updated
WHERE file_path = $oldPath;";
        cmd.Parameters.AddWithValue("$newPath", newPath);
        cmd.Parameters.AddWithValue("$newName", Path.GetFileName(newPath));
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$oldPath", filePath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return newPath;
    }

    public async Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await RemoveFromListAsync(filePath, cancellationToken);
    }

    public async Task RemoveFromListAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM recordings WHERE file_path = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS recordings (
    id TEXT PRIMARY KEY,
    file_path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    title TEXT,
    duration_ms INTEGER NOT NULL,
    sample_rate INTEGER NOT NULL,
    channels INTEGER NOT NULL,
    file_size_bytes INTEGER NOT NULL,
    last_write_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }

        MigrateIfHasLegacyHiddenColumn(connection);
    }

    private static void MigrateIfHasLegacyHiddenColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(recordings);";
        using var reader = check.ExecuteReader();
        var hasHidden = false;
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "is_hidden", StringComparison.OrdinalIgnoreCase))
            {
                hasHidden = true;
                break;
            }
        }

        if (!hasHidden)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
CREATE TABLE recordings_new (
    id TEXT PRIMARY KEY,
    file_path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    title TEXT,
    duration_ms INTEGER NOT NULL,
    sample_rate INTEGER NOT NULL,
    channels INTEGER NOT NULL,
    file_size_bytes INTEGER NOT NULL,
    last_write_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

INSERT INTO recordings_new(
    id, file_path, file_name, title, duration_ms, sample_rate, channels,
    file_size_bytes, last_write_utc, created_utc, updated_utc)
SELECT
    id, file_path, file_name, title, duration_ms, sample_rate, channels,
    file_size_bytes, last_write_utc, created_utc, updated_utc
FROM recordings;

DROP TABLE recordings;
ALTER TABLE recordings_new RENAME TO recordings;";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private static (string title, long durationMs, int sampleRate, int channels) ReadMetadata(string filePath, string fileName)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var title = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
                ? Path.GetFileNameWithoutExtension(fileName)
                : tagFile.Tag.Title.Trim();

            return (
                title,
                (long)tagFile.Properties.Duration.TotalMilliseconds,
                tagFile.Properties.AudioSampleRate,
                tagFile.Properties.AudioChannels);
        }
        catch
        {
            return (Path.GetFileNameWithoutExtension(fileName), 0, 0, 0);
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        string path,
        string fileName,
        string title,
        long durationMs,
        int sampleRate,
        int channels,
        long fileSizeBytes,
        DateTime lastWriteUtc,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO recordings(
    id, file_path, file_name, title, duration_ms, sample_rate, channels,
    file_size_bytes, last_write_utc, created_utc, updated_utc)
VALUES(
    $id, $path, $fileName, $title, $duration, $sampleRate, $channels,
    $size, $lastWrite, $created, $updated)
ON CONFLICT(file_path) DO UPDATE SET
    file_name = excluded.file_name,
    title = excluded.title,
    duration_ms = excluded.duration_ms,
    sample_rate = excluded.sample_rate,
    channels = excluded.channels,
    file_size_bytes = excluded.file_size_bytes,
    last_write_utc = excluded.last_write_utc,
    updated_utc = excluded.updated_utc;";

        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$fileName", fileName);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$duration", durationMs);
        cmd.Parameters.AddWithValue("$sampleRate", sampleRate);
        cmd.Parameters.AddWithValue("$channels", channels);
        cmd.Parameters.AddWithValue("$size", fileSizeBytes);
        cmd.Parameters.AddWithValue("$lastWrite", lastWriteUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LibraryRecordingItem>> GetAllCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, file_path, file_name, title, duration_ms, sample_rate, channels, file_size_bytes, last_write_utc FROM recordings ORDER BY last_write_utc DESC;";

        var list = new List<LibraryRecordingItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new LibraryRecordingItem
            {
                Id = reader.GetString(0),
                FilePath = reader.GetString(1),
                FileName = reader.GetString(2),
                Title = reader.IsDBNull(3) ? Path.GetFileNameWithoutExtension(reader.GetString(2)) : reader.GetString(3),
                DurationMilliseconds = reader.GetInt64(4),
                SampleRate = reader.GetInt32(5),
                Channels = reader.GetInt32(6),
                FileSizeBytes = reader.GetInt64(7),
                LastWriteUtc = DateTime.TryParse(reader.GetString(8), out var dt) ? dt : DateTime.UtcNow
            });
        }

        return list;
    }
}
