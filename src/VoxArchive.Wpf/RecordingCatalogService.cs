using System.IO;
using System.Text;
using System.Text.Json;

namespace VoxArchive.Wpf;

public sealed class RecordingCatalogService
{
    private const int SnapshotSchemaVersion = 1;
    private const int CompactOpCountThreshold = 200;
    private const long CompactOpsSizeThresholdBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _snapshotPath;
    private readonly string _snapshotBackupPath;
    private readonly string _opsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, CatalogEntry>? _interactiveCache;
    private int _interactiveSessionCount;
    private int _opsSinceCompact = -1;

    public RecordingCatalogService(string snapshotPath)
    {
        _snapshotPath = snapshotPath;
        _snapshotBackupPath = snapshotPath + ".bak";
        _opsPath = Path.ChangeExtension(snapshotPath, ".ops.jsonl");

        EnsureStorageDirectory();
    }

    public IDisposable AcquireInteractiveSession()
    {
        Interlocked.Increment(ref _interactiveSessionCount);
        return new CatalogInteractiveSession(this);
    }

    public async Task<IReadOnlyList<LibraryRecordingItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetReadableStateAsync(cancellationToken);
            return ToLibraryItems(state.Values)
                .OrderByDescending(x => x.LastWriteUtc)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddOrUpdateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        var (title, durationMs, sampleRate, channels) = ReadMetadata(filePath, fileInfo.Name);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetWritableStateAsync(cancellationToken);
            var key = NormalizePath(filePath);
            var id = state.TryGetValue(key, out var existing)
                ? existing.Id
                : Guid.NewGuid().ToString("N");

            var entry = new CatalogEntry
            {
                Id = id,
                FilePath = filePath,
                FileName = fileInfo.Name,
                Title = title,
                DurationMilliseconds = durationMs,
                SampleRate = sampleRate,
                Channels = channels,
                FileSizeBytes = fileInfo.Length,
                LastWriteUtc = fileInfo.LastWriteTimeUtc,
                UpdatedUtc = DateTime.UtcNow
            };

            state[key] = entry;
            await AppendOperationAsync(CatalogOperationDto.Upsert(entry), cancellationToken);
            await MaybeCompactAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateTitleAsync(string filePath, string title, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Title = title;
            tagFile.Save();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetWritableStateAsync(cancellationToken);
            var key = NormalizePath(filePath);
            if (!state.TryGetValue(key, out var entry))
            {
                entry = await BuildEntryAsync(filePath, Guid.NewGuid().ToString("N"), cancellationToken);
            }

            entry.Title = title;
            entry.UpdatedUtc = DateTime.UtcNow;
            state[key] = entry;

            await AppendOperationAsync(CatalogOperationDto.Upsert(entry), cancellationToken);
            await MaybeCompactAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RenameAsync(string filePath, string newName, CancellationToken cancellationToken = default)
    {
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetWritableStateAsync(cancellationToken);
            var oldKey = NormalizePath(filePath);
            var newKey = NormalizePath(newPath);
            var id = state.TryGetValue(oldKey, out var existing)
                ? existing.Id
                : Guid.NewGuid().ToString("N");

            var renamed = await BuildEntryAsync(newPath, id, cancellationToken);
            renamed.Title = existing?.Title ?? renamed.Title;
            renamed.UpdatedUtc = DateTime.UtcNow;
            state.Remove(oldKey);
            state[newKey] = renamed;

            await AppendOperationAsync(CatalogOperationDto.Rename(filePath, renamed), cancellationToken);
            await MaybeCompactAsync(state, cancellationToken);
            return newPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        await RemoveFromListAsync(filePath, cancellationToken);
    }

    public async Task RemoveFromListAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetWritableStateAsync(cancellationToken);
            state.Remove(NormalizePath(filePath));

            await AppendOperationAsync(CatalogOperationDto.Remove(filePath), cancellationToken);
            await MaybeCompactAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseInteractiveSession()
    {
        if (Interlocked.Decrement(ref _interactiveSessionCount) > 0)
        {
            return;
        }

        Interlocked.Exchange(ref _interactiveSessionCount, 0);

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_interactiveSessionCount == 0)
                {
                    _interactiveCache = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task<Dictionary<string, CatalogEntry>> GetReadableStateAsync(CancellationToken cancellationToken)
    {
        if (_interactiveSessionCount > 0)
        {
            if (_interactiveCache is null)
            {
                var loaded = await LoadStateAsync(cancellationToken);
                _interactiveCache = loaded.State;
                _opsSinceCompact = loaded.OpsCount;
            }

            return _interactiveCache;
        }

        var transient = await LoadStateAsync(cancellationToken);
        return transient.State;
    }

    private async Task<Dictionary<string, CatalogEntry>> GetWritableStateAsync(CancellationToken cancellationToken)
    {
        if (_interactiveSessionCount > 0)
        {
            if (_interactiveCache is null)
            {
                var loaded = await LoadStateAsync(cancellationToken);
                _interactiveCache = loaded.State;
                _opsSinceCompact = loaded.OpsCount;
            }

            return _interactiveCache;
        }

        var transient = await LoadStateAsync(cancellationToken);
        _opsSinceCompact = transient.OpsCount;
        return transient.State;
    }

    private async Task<(Dictionary<string, CatalogEntry> State, int OpsCount)> LoadStateAsync(CancellationToken cancellationToken)
    {
        var state = await LoadSnapshotAsync(cancellationToken);
        var opsCount = await ReplayOperationsAsync(state, cancellationToken);
        return (state, opsCount);
    }

    private async Task<Dictionary<string, CatalogEntry>> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await TryReadSnapshotFileAsync(_snapshotPath, cancellationToken)
            ?? await TryReadSnapshotFileAsync(_snapshotBackupPath, cancellationToken)
            ?? new CatalogSnapshotDto { SchemaVersion = SnapshotSchemaVersion, Items = [] };

        var state = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in snapshot.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.FilePath) || string.IsNullOrWhiteSpace(dto.FileName))
            {
                continue;
            }

            var entry = dto.ToCatalogEntry();
            state[NormalizePath(entry.FilePath)] = entry;
        }

        return state;
    }

    private static async Task<CatalogSnapshotDto?> TryReadSnapshotFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<CatalogSnapshotDto>(stream, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> ReplayOperationsAsync(Dictionary<string, CatalogEntry> state, CancellationToken cancellationToken)
    {
        if (!File.Exists(_opsPath))
        {
            return 0;
        }

        var opsCount = 0;

        using var stream = new FileStream(_opsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var op = JsonSerializer.Deserialize<CatalogOperationDto>(line, JsonOptions);
                if (op is null)
                {
                    continue;
                }

                ApplyOperation(state, op);
                opsCount++;
            }
            catch
            {
                // 末尾切れなどの壊れた行はスキップして継続する。
            }
        }

        return opsCount;
    }

    private static void ApplyOperation(Dictionary<string, CatalogEntry> state, CatalogOperationDto operation)
    {
        if (string.Equals(operation.Op, "remove", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(operation.FilePath))
            {
                state.Remove(NormalizePath(operation.FilePath));
            }

            return;
        }

        if (string.Equals(operation.Op, "rename", StringComparison.OrdinalIgnoreCase) && operation.Entry is not null)
        {
            if (!string.IsNullOrWhiteSpace(operation.FilePath))
            {
                state.Remove(NormalizePath(operation.FilePath));
            }

            var renamedEntry = operation.Entry.ToCatalogEntry();
            if (!string.IsNullOrWhiteSpace(renamedEntry.FilePath) && !string.IsNullOrWhiteSpace(renamedEntry.FileName))
            {
                state[NormalizePath(renamedEntry.FilePath)] = renamedEntry;
            }

            return;
        }

        if (!string.Equals(operation.Op, "upsert", StringComparison.OrdinalIgnoreCase) || operation.Entry is null)
        {
            return;
        }

        var entry = operation.Entry.ToCatalogEntry();
        if (string.IsNullOrWhiteSpace(entry.FilePath) || string.IsNullOrWhiteSpace(entry.FileName))
        {
            return;
        }

        state[NormalizePath(entry.FilePath)] = entry;
    }

    private async Task AppendOperationAsync(CatalogOperationDto operation, CancellationToken cancellationToken)
    {
        EnsureStorageDirectory();

        var line = JsonSerializer.Serialize(operation, JsonOptions) + Environment.NewLine;
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);

        await using var stream = new FileStream(_opsPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);

        _opsSinceCompact = _opsSinceCompact < 0 ? 1 : _opsSinceCompact + 1;
    }

    private async Task MaybeCompactAsync(Dictionary<string, CatalogEntry> state, CancellationToken cancellationToken)
    {
        var opsInfo = new FileInfo(_opsPath);
        var shouldCompact = (_opsSinceCompact >= CompactOpCountThreshold)
                            || (opsInfo.Exists && opsInfo.Length >= CompactOpsSizeThresholdBytes);

        if (!shouldCompact)
        {
            return;
        }

        var snapshot = new CatalogSnapshotDto
        {
            SchemaVersion = SnapshotSchemaVersion,
            Items = state.Values
                .OrderByDescending(x => x.LastWriteUtc)
                .Select(CatalogItemDto.FromCatalogEntry)
                .ToList()
        };

        await WriteSnapshotAtomicallyAsync(snapshot, cancellationToken);
        await ResetOperationsLogAsync(cancellationToken);
        _opsSinceCompact = 0;
    }

    private async Task WriteSnapshotAtomicallyAsync(CatalogSnapshotDto snapshot, CancellationToken cancellationToken)
    {
        EnsureStorageDirectory();

        var tempPath = _snapshotPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_snapshotPath))
        {
            File.Replace(tempPath, _snapshotPath, _snapshotBackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _snapshotPath);
        }
    }

    private async Task ResetOperationsLogAsync(CancellationToken cancellationToken)
    {
        var tempPath = _opsPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_opsPath))
        {
            File.Replace(tempPath, _opsPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _opsPath);
        }
    }

    private async Task<CatalogEntry> BuildEntryAsync(string filePath, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("録音ファイルが見つかりません。", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        var (title, durationMs, sampleRate, channels) = ReadMetadata(filePath, fileInfo.Name);

        return new CatalogEntry
        {
            Id = id,
            FilePath = filePath,
            FileName = fileInfo.Name,
            Title = title,
            DurationMilliseconds = durationMs,
            SampleRate = sampleRate,
            Channels = channels,
            FileSizeBytes = fileInfo.Length,
            LastWriteUtc = fileInfo.LastWriteTimeUtc,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static IReadOnlyList<LibraryRecordingItem> ToLibraryItems(IEnumerable<CatalogEntry> entries)
    {
        return entries.Select(x => new LibraryRecordingItem
        {
            Id = x.Id,
            FilePath = x.FilePath,
            FileName = x.FileName,
            Title = x.Title,
            DurationMilliseconds = x.DurationMilliseconds,
            SampleRate = x.SampleRate,
            Channels = x.Channels,
            FileSizeBytes = x.FileSizeBytes,
            LastWriteUtc = x.LastWriteUtc
        }).ToList();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).Trim();
        }
        catch
        {
            return path.Trim();
        }
    }

    private void EnsureStorageDirectory()
    {
        var dir = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
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

    private sealed class CatalogInteractiveSession : IDisposable
    {
        private readonly RecordingCatalogService _owner;
        private int _disposed;

        public CatalogInteractiveSession(RecordingCatalogService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.ReleaseInteractiveSession();
        }
    }

    private sealed class CatalogEntry
    {
        public required string Id { get; init; }
        public required string FilePath { get; set; }
        public required string FileName { get; set; }
        public required string Title { get; set; }
        public long DurationMilliseconds { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public long FileSizeBytes { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class CatalogSnapshotDto
    {
        public int SchemaVersion { get; set; }
        public List<CatalogItemDto> Items { get; set; } = [];
    }

    private sealed class CatalogItemDto
    {
        public required string Id { get; set; }
        public required string FilePath { get; set; }
        public required string FileName { get; set; }
        public required string Title { get; set; }
        public long DurationMilliseconds { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public long FileSizeBytes { get; set; }
        public string LastWriteUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public string UpdatedUtc { get; set; } = DateTime.UtcNow.ToString("o");

        public static CatalogItemDto FromCatalogEntry(CatalogEntry entry)
        {
            return new CatalogItemDto
            {
                Id = entry.Id,
                FilePath = entry.FilePath,
                FileName = entry.FileName,
                Title = entry.Title,
                DurationMilliseconds = entry.DurationMilliseconds,
                SampleRate = entry.SampleRate,
                Channels = entry.Channels,
                FileSizeBytes = entry.FileSizeBytes,
                LastWriteUtc = entry.LastWriteUtc.ToString("o"),
                UpdatedUtc = entry.UpdatedUtc.ToString("o")
            };
        }

        public CatalogEntry ToCatalogEntry()
        {
            return new CatalogEntry
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                FilePath = FilePath,
                FileName = FileName,
                Title = string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FileName) : Title,
                DurationMilliseconds = DurationMilliseconds,
                SampleRate = SampleRate,
                Channels = Channels,
                FileSizeBytes = FileSizeBytes,
                LastWriteUtc = DateTime.TryParse(LastWriteUtc, out var lastWrite) ? lastWrite : DateTime.UtcNow,
                UpdatedUtc = DateTime.TryParse(UpdatedUtc, out var updated) ? updated : DateTime.UtcNow
            };
        }
    }

    private sealed class CatalogOperationDto
    {
        public required string Op { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public CatalogItemDto? Entry { get; set; }

        public static CatalogOperationDto Upsert(CatalogEntry entry)
        {
            return new CatalogOperationDto
            {
                Op = "upsert",
                Entry = CatalogItemDto.FromCatalogEntry(entry)
            };
        }

        public static CatalogOperationDto Remove(string filePath)
        {
            return new CatalogOperationDto
            {
                Op = "remove",
                FilePath = filePath
            };
        }

        public static CatalogOperationDto Rename(string oldFilePath, CatalogEntry entry)
        {
            return new CatalogOperationDto
            {
                Op = "rename",
                FilePath = oldFilePath,
                Entry = CatalogItemDto.FromCatalogEntry(entry)
            };
        }
    }
}

