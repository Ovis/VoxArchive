using System.Security.Cryptography;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine固有catalogに従い、モデルパッケージを検証しながらアトミックに配置する
/// </summary>
public sealed class TranscriptionModelPackageInstaller(HttpClient httpClient)
{
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// 一時領域のbest effort削除に失敗した場合の通知先
    /// </summary>
    public Action<string, Exception>? CleanupFailureHandler { get; set; }

    /// <summary>
    /// 指定レベルでモデル配置状態を確認する
    /// </summary>
    public TranscriptionModelPackageState Inspect(
        TranscriptionModelPackageDefinition definition,
        string installationDirectory,
        TranscriptionModelInspectionLevel level)
    {
        ValidateDefinition(definition);
        var existingFiles = 0;
        foreach (var file in definition.Files)
        {
            var path = Path.Combine(installationDirectory, file.DestinationName);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                continue;
            }

            existingFiles++;
            if (level >= TranscriptionModelInspectionLevel.Size && info.Length != file.Size)
            {
                return TranscriptionModelPackageState.Corrupt;
            }
            if (level >= TranscriptionModelInspectionLevel.Hash && !ValidateHash(file, path))
            {
                return TranscriptionModelPackageState.Corrupt;
            }
        }

        if (existingFiles == 0) return TranscriptionModelPackageState.Missing;
        if (existingFiles != definition.Files.Count) return TranscriptionModelPackageState.Incomplete;
        return TranscriptionModelPackageState.Installed;
    }

    /// <summary>
    /// モデルをstagingへ取得し、全ファイルのサイズ/SHA検証後にディレクトリ単位で確定する
    /// </summary>
    public async Task<string> InstallAsync(
        TranscriptionModelPackageDefinition definition,
        string modelsRootDirectory,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        var engineDirectory = Path.Combine(modelsRootDirectory, definition.EngineId.Value);
        var destinationDirectory = Path.Combine(engineDirectory, definition.ModelId.Value);
        if (!force && Inspect(definition, destinationDirectory, TranscriptionModelInspectionLevel.Hash)
            == TranscriptionModelPackageState.Installed)
        {
            return destinationDirectory;
        }

        Directory.CreateDirectory(engineDirectory);
        var downloadsRoot = Path.Combine(modelsRootDirectory, ".downloads");
        Directory.CreateDirectory(downloadsRoot);
        var stagingDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N") + ".backup");
        Directory.CreateDirectory(stagingDirectory);

        var totalBytes = definition.Files.Sum(x => x.Size);
        long transferred = 0;
        progress?.Report(new TranscriptionModelTransferProgress(0, totalBytes));

        try
        {
            foreach (var file in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = Path.Combine(stagingDirectory, file.DestinationName);
                var parent = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                using (var response = await httpClient.GetAsync(
                           file.SourceUrl,
                           HttpCompletionOption.ResponseHeadersRead,
                           cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var destination = new FileStream(
                        stagingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        CopyBufferSize,
                        useAsync: true);
                    var buffer = new byte[CopyBufferSize];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                        if (read == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        transferred += read;
                        progress?.Report(new TranscriptionModelTransferProgress(transferred, totalBytes));
                    }
                }

                // FileStreamを閉じる前にSHAを読むと、OS/managed bufferへ残った未flushデータを検証する可能性がある。
                // 取得ストリームを完全に破棄してからサイズ/SHAを確認し、検証済みのstagingだけを公開対象にする。
                if (!ValidateInstalledFile(file, stagingPath))
                {
                    throw new InvalidDataException($"モデルファイルの検証に失敗しました: {file.DestinationName}");
                }
            }

            // 更新時に旧readyモデルを先に失わないよう、同一ボリューム上へ退避してから新配置を確定する。
            if (Directory.Exists(destinationDirectory)) Directory.Move(destinationDirectory, backupDirectory);
            try
            {
                Directory.Move(stagingDirectory, destinationDirectory);
            }
            catch
            {
                if (Directory.Exists(backupDirectory) && !Directory.Exists(destinationDirectory))
                {
                    Directory.Move(backupDirectory, destinationDirectory);
                }
                throw;
            }

            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            progress?.Report(new TranscriptionModelTransferProgress(totalBytes, totalBytes));
            return destinationDirectory;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    /// <summary>
    /// 指定モデルの確定ディレクトリを削除する
    /// </summary>
    public static void Delete(TranscriptionModelPackageDefinition definition, string modelsRootDirectory)
    {
        var path = Path.Combine(modelsRootDirectory, definition.EngineId.Value, definition.ModelId.Value);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static bool ValidateInstalledFile(TranscriptionModelFileDefinition definition, string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == definition.Size && ValidateHash(definition, path);
    }

    private static bool ValidateHash(TranscriptionModelFileDefinition definition, string path)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(stream)),
            definition.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // stagingは確定先と分離されているため、掃除失敗で本来の取得例外を上書きしない。
            CleanupFailureHandler?.Invoke(path, ex);
        }
    }

    private static void ValidateDefinition(TranscriptionModelPackageDefinition definition)
    {
        if (definition.Files.Count == 0)
        {
            throw new ArgumentException("モデル定義には1つ以上のファイルが必要です。", nameof(definition));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in definition.Files)
        {
            if (file.Size < 0) throw new ArgumentException($"ファイルサイズが不正です: {file.DestinationName}", nameof(definition));
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException($"SHA-256が不正です: {file.DestinationName}", nameof(definition));
            if (Path.IsPathRooted(file.DestinationName) || file.DestinationName.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"配置先ファイル名が不正です: {file.DestinationName}", nameof(definition));
            if (!names.Add(file.DestinationName))
                throw new ArgumentException($"配置先ファイル名が重複しています: {file.DestinationName}", nameof(definition));
        }
    }
}
