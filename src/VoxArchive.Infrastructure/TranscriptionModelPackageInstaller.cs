using System.Security.Cryptography;

namespace VoxArchive.Infrastructure;

/// <summary>
/// モデル配置状態を確認するときの検証レベルを表す
/// </summary>
public enum TranscriptionModelInspectionLevel
{
    /// <summary>必要ファイルが存在するかだけを確認する</summary>
    Existence,

    /// <summary>存在確認に加えて期待サイズを確認する</summary>
    Size,

    /// <summary>存在・サイズ・SHA-256をすべて確認する</summary>
    Hash
}

/// <summary>
/// モデルパッケージの配置状態を表す
/// </summary>
public enum TranscriptionModelPackageState
{
    Missing,
    Installed,
    Incomplete,
    Corrupt
}

/// <summary>
/// モデル取得全体の転送進捗を表す
/// </summary>
/// <param name="BytesReceived">取得済みバイト数</param>
/// <param name="TotalBytes">モデルパッケージ全体の期待バイト数</param>
public sealed record TranscriptionModelTransferProgress(long BytesReceived, long TotalBytes)
{
    /// <summary>0～100の進捗率を取得する</summary>
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}

/// <summary>
/// 複数ファイルで構成される文字起こしモデルを検証しながらアトミックに配置する
/// </summary>
/// <remarks>
/// ダウンロード途中のファイルを配置済みモデルとして観測させないため、全ファイルを一時ディレクトリへ取得して
/// サイズとSHA-256を検証した後にディレクトリ単位で確定先へ移動する。
/// </remarks>
public sealed class TranscriptionModelPackageInstaller
{
    private const int CopyBufferSize = 81920;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// モデル取得に利用するHTTPクライアントを指定して初期化する
    /// </summary>
    /// <param name="httpClient">モデル配布元へアクセスするHTTPクライアント</param>
    public TranscriptionModelPackageInstaller(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 一時領域のbest effort削除に失敗した場合の通知先を取得・設定する
    /// </summary>
    /// <remarks>
    /// クリーンアップ失敗は本来の取得例外を置き換えないため例外として再送出せず、
    /// アプリケーション層が診断ログへ記録できるよう補助通知だけを提供する。
    /// </remarks>
    public Action<string, Exception>? CleanupFailureHandler { get; set; }

    /// <summary>
    /// モデル定義に含まれる全ファイルが正しい状態で配置されているか確認する
    /// </summary>
    public bool IsInstalled(TranscriptionModelDefinition definition, string installationDirectory)
    {
        return Inspect(definition, installationDirectory, TranscriptionModelInspectionLevel.Hash) == TranscriptionModelPackageState.Installed;
    }

    /// <summary>
    /// 指定した検証レベルでモデルの配置状態を確認する
    /// </summary>
    /// <param name="definition">確認対象の固定モデル定義</param>
    /// <param name="installationDirectory">モデルの確定配置ディレクトリ</param>
    /// <param name="level">存在・サイズ・SHA-256のどこまで確認するか</param>
    public TranscriptionModelPackageState Inspect(
        TranscriptionModelDefinition definition,
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

            if (level >= TranscriptionModelInspectionLevel.Hash && !ValidateInstalledFileHash(file, path))
            {
                return TranscriptionModelPackageState.Corrupt;
            }
        }

        if (existingFiles == 0)
        {
            return TranscriptionModelPackageState.Missing;
        }

        if (existingFiles != definition.Files.Count)
        {
            return TranscriptionModelPackageState.Incomplete;
        }

        return TranscriptionModelPackageState.Installed;
    }

    /// <summary>
    /// モデルを一時領域へ取得・検証し、検証成功後に確定ディレクトリへ配置する
    /// </summary>
    /// <param name="definition">取得するモデルの固定定義</param>
    /// <param name="modelsRootDirectory">Engine別モデルディレクトリを配置するルート</param>
    /// <param name="cancellationToken">取得処理を中断するトークン</param>
    /// <returns>確定したモデルディレクトリ</returns>
    public Task<string> InstallAsync(
        TranscriptionModelDefinition definition,
        string modelsRootDirectory,
        CancellationToken cancellationToken = default)
    {
        return InstallAsync(definition, modelsRootDirectory, force: false, progress: null, cancellationToken);
    }

    /// <summary>
    /// モデルを一時領域へ取得し、パッケージ全体の進捗を通知しながらアトミックに配置する
    /// </summary>
    /// <param name="definition">取得するモデルの固定定義</param>
    /// <param name="modelsRootDirectory">Engine別モデルディレクトリを配置するルート</param>
    /// <param name="force">既存モデルが完全でも再取得する場合はtrue</param>
    /// <param name="progress">パッケージ全体の転送進捗通知先</param>
    /// <param name="cancellationToken">取得処理を中断するトークン</param>
    public async Task<string> InstallAsync(
        TranscriptionModelDefinition definition,
        string modelsRootDirectory,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);

        var engineDirectory = Path.Combine(modelsRootDirectory, definition.EngineId.Value);
        var destinationDirectory = Path.Combine(engineDirectory, definition.ModelId.Value);
        if (!force && IsInstalled(definition, destinationDirectory))
        {
            return destinationDirectory;
        }

        Directory.CreateDirectory(engineDirectory);
        var downloadsRoot = Path.Combine(modelsRootDirectory, ".downloads");
        Directory.CreateDirectory(downloadsRoot);
        var stagingDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N") + ".backup");
        Directory.CreateDirectory(stagingDirectory);

        var totalBytes = definition.Files.Sum(file => file.Size);
        long transferredBytes = 0;
        progress?.Report(new TranscriptionModelTransferProgress(0, totalBytes));

        try
        {
            foreach (var file in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = Path.Combine(stagingDirectory, file.DestinationName);
                var parentDirectory = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                using var response = await _httpClient.GetAsync(file.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
                {
                    var buffer = new byte[CopyBufferSize];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        transferredBytes += read;
                        progress?.Report(new TranscriptionModelTransferProgress(transferredBytes, totalBytes));
                    }
                }

                if (!ValidateInstalledFile(file, stagingPath))
                {
                    throw new InvalidDataException($"モデルファイルの検証に失敗しました: {file.DestinationName}");
                }
            }

            // 更新時も旧モデルを先に破棄しない。旧配置を同一ボリューム上の退避先へ移してから新配置を確定し、
            // 新配置の確定に失敗した場合は旧配置を戻すことで利用可能なモデルを失わないようにする。
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Move(destinationDirectory, backupDirectory);
            }

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

            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }

            progress?.Report(new TranscriptionModelTransferProgress(totalBytes, totalBytes));
            return destinationDirectory;
        }
        catch
        {
            // 元の取得失敗をstaging削除失敗で上書きしない。残骸は確定先とは別の.downloads配下なので、
            // 削除に失敗しても配置済みモデルとして誤認されることはない。
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    /// <summary>
    /// 指定したモデルの確定ディレクトリを削除する
    /// </summary>
    public static void Delete(TranscriptionModelDefinition definition, string modelsRootDirectory)
    {
        var destinationDirectory = Path.Combine(modelsRootDirectory, definition.EngineId.Value, definition.ModelId.Value);
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }
    }

    private static bool ValidateInstalledFile(TranscriptionModelFileDefinition definition, string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            && info.Length == definition.Size
            && ValidateInstalledFileHash(definition, path);
    }

    private static bool ValidateInstalledFileHash(TranscriptionModelFileDefinition definition, string path)
    {
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actualHash, definition.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // ユーザー操作を追加で妨げず、アプリケーション層の診断ログへだけ残せるよう通知する。
            CleanupFailureHandler?.Invoke(path, ex);
        }
    }

    private static void ValidateDefinition(TranscriptionModelDefinition definition)
    {
        if (definition.Files.Count == 0)
        {
            throw new ArgumentException("モデル定義には1つ以上のファイルが必要です。", nameof(definition));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in definition.Files)
        {
            if (file.Size < 0)
            {
                throw new ArgumentException($"ファイルサイズが不正です: {file.DestinationName}", nameof(definition));
            }

            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new ArgumentException($"SHA-256が不正です: {file.DestinationName}", nameof(definition));
            }

            if (Path.IsPathRooted(file.DestinationName) || file.DestinationName.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException($"配置先ファイル名が不正です: {file.DestinationName}", nameof(definition));
            }

            if (!names.Add(file.DestinationName))
            {
                throw new ArgumentException($"配置先ファイル名が重複しています: {file.DestinationName}", nameof(definition));
            }
        }
    }
}
