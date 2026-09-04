using System.Security.Cryptography;

namespace VoxArchive.Infrastructure;

/// <summary>
/// 複数ファイルで構成される文字起こしモデルを検証しながらアトミックに配置する
/// </summary>
/// <remarks>
/// ダウンロード途中のファイルを配置済みモデルとして観測させないため、全ファイルを一時ディレクトリへ取得して
/// サイズとSHA-256を検証した後にディレクトリ単位で確定先へ移動する。
/// </remarks>
public sealed class TranscriptionModelPackageInstaller
{
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
    /// モデル定義に含まれる全ファイルが正しい状態で配置されているか確認する
    /// </summary>
    public bool IsInstalled(TranscriptionModelDefinition definition, string installationDirectory)
    {
        ValidateDefinition(definition);
        return definition.Files.All(file => ValidateInstalledFile(file, Path.Combine(installationDirectory, file.DestinationName)));
    }

    /// <summary>
    /// モデルを一時領域へ取得・検証し、検証成功後に確定ディレクトリへ配置する
    /// </summary>
    /// <param name="definition">取得するモデルの固定定義</param>
    /// <param name="modelsRootDirectory">Engine別モデルディレクトリを配置するルート</param>
    /// <param name="cancellationToken">取得処理を中断するトークン</param>
    /// <returns>確定したモデルディレクトリ</returns>
    public async Task<string> InstallAsync(
        TranscriptionModelDefinition definition,
        string modelsRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);

        var engineDirectory = Path.Combine(modelsRootDirectory, definition.EngineId.Value);
        var destinationDirectory = Path.Combine(engineDirectory, definition.ModelId.Value);
        if (IsInstalled(definition, destinationDirectory))
        {
            return destinationDirectory;
        }

        Directory.CreateDirectory(engineDirectory);
        var downloadsRoot = Path.Combine(modelsRootDirectory, ".downloads");
        Directory.CreateDirectory(downloadsRoot);
        var stagingDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(downloadsRoot, Guid.NewGuid().ToString("N") + ".backup");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            foreach (var file in definition.Files)
            {
                var stagingPath = Path.Combine(stagingDirectory, file.DestinationName);
                var parentDirectory = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                using var response = await _httpClient.GetAsync(file.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await source.CopyToAsync(destination, cancellationToken);
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

            return destinationDirectory;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

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
        if (!info.Exists || info.Length != definition.Size)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actualHash, definition.Sha256, StringComparison.OrdinalIgnoreCase);
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
