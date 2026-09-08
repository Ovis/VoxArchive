using System.Text.Json;

namespace VoxArchive.Transcription;

/// <summary>
/// モデル固有形式を知らず、download・staging・validation・atomic commitの安全なライフサイクルだけを提供する
/// </summary>
/// <remarks>
/// Silero/ReazonSpeechはSHA-256ではなく実際のnative load成功を利用可能条件とするため、
/// 検証内容は呼び出し側へ委譲する。既存の正常モデルは新モデルが正式配置でも利用可能と確認できるまで維持する。
/// </remarks>
public sealed class ManagedModelFileTransaction(HttpClient httpClient)
{
    private const int CopyBufferSize = 81920;
    private const string BackupPrefix = "backup-";
    private const string BackupRecoveryManifestSuffix = ".recovery.json";
    private const string BackupCommittedMarkerSuffix = ".committed";
    private static readonly string[] DisposableTemporaryPrefixes = ["download-", "delete-"];

    /// <summary>一時領域のbest effort削除に失敗した場合の通知先</summary>
    public Action<string, Exception>? CleanupFailureHandler { get; set; }

    /// <summary>
    /// 必要ファイルを一時領域へ取得し、モデル固有validation成功後に確定先へatomic commitする
    /// </summary>
    /// <param name="files">今回の設定で必要な取得対象だけを指定する</param>
    /// <param name="destinationDirectory">検証済みモデルを公開する確定ディレクトリ</param>
    /// <param name="temporaryRootDirectory">VoxArchiveが所有する同一ファイルシステム上の一時領域</param>
    /// <param name="validateStagingAsync">staging上のモデルを実際にloadして利用可否を確認する処理</param>
    /// <param name="progress">取得・検証状態の通知先</param>
    /// <param name="cancellationToken">downloadは即時キャンセルし、native validation中は終了後にcommitを抑止する</param>
    /// <param name="validateCommittedAsync">正式配置へrenameしたモデルを最終確認する処理。失敗時は旧モデルへrollbackする</param>
    public async Task<string> DownloadValidateCommitAsync(
        IReadOnlyList<ManagedModelDownloadFile> files,
        string destinationDirectory,
        string temporaryRootDirectory,
        Func<string, Task> validateStagingAsync,
        IProgress<ManagedModelTransactionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, Task>? validateCommittedAsync = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRootDirectory);
        ArgumentNullException.ThrowIfNull(validateStagingAsync);
        if (files.Count == 0) throw new ArgumentException("モデル取得対象がありません。", nameof(files));
        ValidateFiles(files);

        Directory.CreateDirectory(temporaryRootDirectory);
        var stagingDirectory = Path.Combine(temporaryRootDirectory, $"download-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(temporaryRootDirectory, $"{BackupPrefix}{Guid.NewGuid():N}");
        var recoveryManifestPath = GetRecoveryManifestPath(backupDirectory);
        var committedMarkerPath = GetCommittedMarkerPath(backupDirectory);
        Directory.CreateDirectory(stagingDirectory);

        long transferred = 0;
        var totalBytes = await TryResolveTotalBytesAsync(files, cancellationToken);

        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ManagedModelTransactionProgress(
                    transferred,
                    totalBytes,
                    file.DestinationName,
                    IsValidating: false));

                var stagingPath = Path.Combine(stagingDirectory, file.DestinationName);
                var parent = Path.GetDirectoryName(stagingPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                // 取得ストリームを完全に破棄してからサイズやnative loadを確認する。
                // FileStreamがmanaged bufferを保持したままFileInfo.Lengthを見ると、正常取得でも短いと誤判定する可能性がある。
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
                        progress?.Report(new ManagedModelTransactionProgress(
                            transferred,
                            totalBytes,
                            file.DestinationName,
                            IsValidating: false));
                    }
                }

                // SHA-256は使用しないが、配布元が固定サイズを定義している場合は明白な途中切断だけを検出する。
                if (file.ExpectedSizeBytes is { } expectedSize
                    && new FileInfo(stagingPath).Length != expectedSize)
                {
                    throw new InvalidDataException($"モデルファイルのサイズが一致しません: {file.DestinationName}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ManagedModelTransactionProgress(
                transferred,
                totalBytes,
                CurrentFileName: null,
                IsValidating: true));

            // native model loadは安全に強制停止できないためCancellationTokenを渡さない。
            // validation完了後にキャンセル要求を再確認し、要求済みなら確定先へcommitしない。
            await validateStagingAsync(stagingDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            var destinationParent = Path.GetDirectoryName(destinationDirectory);
            if (!string.IsNullOrWhiteSpace(destinationParent)) Directory.CreateDirectory(destinationParent);

            // 旧モデルを退避する前に復旧先を記録する。Directory.Move直後にprocessが終了しても、
            // 次回起動時にbackupを単なる残骸として削除せず正式位置へ戻せる状態を先に作る。
            var hadExistingModel = Directory.Exists(destinationDirectory);
            if (hadExistingModel)
            {
                WriteRecoveryManifest(recoveryManifestPath, destinationDirectory);
                Directory.Move(destinationDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, destinationDirectory);
                if (validateCommittedAsync is not null)
                {
                    await validateCommittedAsync(destinationDirectory);
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (hadExistingModel)
                {
                    // marker作成後は新モデルを正式commit済みとみなす。ここからprocessが終了した場合は
                    // 次回起動で旧backupを削除し、新モデルを維持する。
                    File.WriteAllText(committedMarkerPath, string.Empty);
                }
            }
            catch
            {
                RollbackCommittedDirectory(
                    destinationDirectory,
                    backupDirectory,
                    temporaryRootDirectory,
                    recoveryManifestPath,
                    committedMarkerPath);
                throw;
            }

            TryDeleteDirectory(backupDirectory);
            TryDeleteFile(recoveryManifestPath);
            TryDeleteFile(committedMarkerPath);
            return destinationDirectory;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    /// <summary>
    /// 確定モデルを同一ファイルシステム上の削除用一時名へrenameしてから物理削除する
    /// </summary>
    /// <remarks>
    /// renameに失敗した場合は確定ディレクトリを変更せず例外を返す。部分削除を避けるため、
    /// 個別ファイル削除へのfallbackは行わない。rename成功後の物理削除失敗は利用可能パスから既に隔離済みなので、
    /// CleanupFailureHandlerへ記録し次回cleanup対象として残す。
    /// </remarks>
    public void DeleteAtomically(string destinationDirectory, string temporaryRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRootDirectory);
        if (!Directory.Exists(destinationDirectory)) return;

        Directory.CreateDirectory(temporaryRootDirectory);
        var deletionDirectory = Path.Combine(temporaryRootDirectory, $"delete-{Guid.NewGuid():N}");

        // Directory.Move失敗時はここで処理を中断する。確定モデルへ手を入れる別手段へfallbackしない。
        Directory.Move(destinationDirectory, deletionDirectory);
        TryDeleteDirectory(deletionDirectory);
    }

    /// <summary>
    /// 前回異常終了などで残ったVoxArchive管理のモデルtransactionを復旧し、安全に削除できる一時領域だけを掃除する
    /// </summary>
    /// <remarks>
    /// backupはdownload/delete残骸と異なり、異常終了時には最後の正常モデルそのものである可能性がある。
    /// recovery manifestとcommit markerを確認し、未commitなら旧モデルを正式位置へ戻し、commit済みならbackupだけを削除する。
    /// </remarks>
    public void CleanupOwnedTemporaryDirectories(string temporaryRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRootDirectory);
        if (!Directory.Exists(temporaryRootDirectory)) return;

        foreach (var backupDirectory in Directory.EnumerateDirectories(temporaryRootDirectory, $"{BackupPrefix}*"))
        {
            var name = Path.GetFileName(backupDirectory);
            if (!IsOwnedBackupDirectoryName(name)) continue;
            RecoverOrCleanupBackup(backupDirectory, temporaryRootDirectory);
        }

        foreach (var directory in Directory.EnumerateDirectories(temporaryRootDirectory))
        {
            var name = Path.GetFileName(directory);
            if (!IsDisposableTemporaryDirectoryName(name)) continue;
            TryDeleteDirectory(directory);
        }

        // backup本体が残っていないmanifest/markerは、backup移動前またはcleanup途中の異常終了で残った補助ファイルなので削除してよい。
        foreach (var manifestPath in Directory.EnumerateFiles(temporaryRootDirectory, $"{BackupPrefix}*{BackupRecoveryManifestSuffix}"))
        {
            var backupDirectory = manifestPath[..^BackupRecoveryManifestSuffix.Length];
            if (!Directory.Exists(backupDirectory)) TryDeleteFile(manifestPath);
        }
        foreach (var markerPath in Directory.EnumerateFiles(temporaryRootDirectory, $"{BackupPrefix}*{BackupCommittedMarkerSuffix}"))
        {
            var backupDirectory = markerPath[..^BackupCommittedMarkerSuffix.Length];
            if (!Directory.Exists(backupDirectory)) TryDeleteFile(markerPath);
        }
    }

    private async Task<long?> TryResolveTotalBytesAsync(
        IReadOnlyList<ManagedModelDownloadFile> files,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var file in files)
        {
            if (file.ExpectedSizeBytes is { } expected)
            {
                total = checked(total + expected);
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, file.SourceUrl);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is not { } contentLength)
                {
                    return null;
                }

                total = checked(total + contentLength);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Content-Length取得は進捗表示の補助情報であり、失敗してもモデル取得自体を止めない。
                return null;
            }
        }

        return total;
    }

    private void RecoverOrCleanupBackup(string backupDirectory, string temporaryRootDirectory)
    {
        var recoveryManifestPath = GetRecoveryManifestPath(backupDirectory);
        var committedMarkerPath = GetCommittedMarkerPath(backupDirectory);

        if (!File.Exists(recoveryManifestPath))
        {
            // 復旧先が分からないbackupを推測で削除すると、旧正常モデルを失う可能性がある。
            // 旧バージョン由来などの不明なbackupは保全し、cleanup失敗としてログへ残す。
            CleanupFailureHandler?.Invoke(
                backupDirectory,
                new InvalidDataException("モデルbackupのrecovery manifestが見つからないため自動削除しません。"));
            return;
        }

        BackupRecoveryManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupRecoveryManifest>(File.ReadAllText(recoveryManifestPath))
                ?? throw new InvalidDataException("モデルbackupのrecovery manifestを読み込めません。");
            if (string.IsNullOrWhiteSpace(manifest.DestinationDirectory))
            {
                throw new InvalidDataException("モデルbackupの復旧先が空です。");
            }
        }
        catch (Exception ex)
        {
            // manifest破損時もbackup自体は削除しない。人手で復旧できる最後の正常モデルである可能性を優先する。
            CleanupFailureHandler?.Invoke(backupDirectory, ex);
            return;
        }

        if (File.Exists(committedMarkerPath))
        {
            // 最終validationとcancel確認を通過した後のmarkerなので、新正式モデルを正本としてbackupだけを掃除する。
            TryDeleteDirectory(backupDirectory);
            if (!Directory.Exists(backupDirectory))
            {
                TryDeleteFile(recoveryManifestPath);
                TryDeleteFile(committedMarkerPath);
            }
            return;
        }

        try
        {
            // markerがないbackupはtransaction未commitである。新正式モデルが存在していても途中配置の可能性があるため
            // 先にdelete領域へ隔離してから、最後に確認済みだった旧backupを正式位置へ戻す。
            if (Directory.Exists(manifest.DestinationDirectory))
            {
                var failedDirectory = Path.Combine(temporaryRootDirectory, $"delete-{Guid.NewGuid():N}");
                Directory.Move(manifest.DestinationDirectory, failedDirectory);
                TryDeleteDirectory(failedDirectory);
            }

            if (!Directory.Exists(manifest.DestinationDirectory))
            {
                Directory.Move(backupDirectory, manifest.DestinationDirectory);
            }

            TryDeleteFile(recoveryManifestPath);
            TryDeleteFile(committedMarkerPath);
        }
        catch (Exception ex)
        {
            CleanupFailureHandler?.Invoke(backupDirectory, ex);
        }
    }

    private void RollbackCommittedDirectory(
        string destinationDirectory,
        string backupDirectory,
        string temporaryRootDirectory,
        string recoveryManifestPath,
        string committedMarkerPath)
    {
        // 失敗した新モデルを正式パスから先に隔離する。直接recursive deleteして途中失敗すると
        // 旧モデルを正式パスへ戻せないため、safe deleteと同じrename境界を利用する。
        if (Directory.Exists(destinationDirectory))
        {
            var failedDirectory = Path.Combine(temporaryRootDirectory, $"delete-{Guid.NewGuid():N}");
            Directory.Move(destinationDirectory, failedDirectory);
            TryDeleteDirectory(failedDirectory);
        }

        if (Directory.Exists(backupDirectory) && !Directory.Exists(destinationDirectory))
        {
            Directory.Move(backupDirectory, destinationDirectory);
        }

        TryDeleteFile(recoveryManifestPath);
        TryDeleteFile(committedMarkerPath);
    }

    private static void WriteRecoveryManifest(string path, string destinationDirectory)
    {
        var manifest = new BackupRecoveryManifest(Path.GetFullPath(destinationDirectory));
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
    }

    private static string GetRecoveryManifestPath(string backupDirectory)
        => backupDirectory + BackupRecoveryManifestSuffix;

    private static string GetCommittedMarkerPath(string backupDirectory)
        => backupDirectory + BackupCommittedMarkerSuffix;

    private static bool IsOwnedBackupDirectoryName(string name)
    {
        if (!name.StartsWith(BackupPrefix, StringComparison.Ordinal)) return false;
        return Guid.TryParseExact(name[BackupPrefix.Length..], "N", out _);
    }

    private static bool IsDisposableTemporaryDirectoryName(string name)
    {
        foreach (var prefix in DisposableTemporaryPrefixes)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = name[prefix.Length..];
            return Guid.TryParseExact(suffix, "N", out _);
        }
        return false;
    }

    private static void ValidateFiles(IReadOnlyList<ManagedModelDownloadFile> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.ExpectedSizeBytes is < 0)
                throw new ArgumentException($"モデルファイルサイズが不正です: {file.DestinationName}", nameof(files));
            if (Path.IsPathRooted(file.DestinationName)
                || file.DestinationName.Contains("..", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(file.DestinationName))
                throw new ArgumentException($"配置先ファイル名が不正です: {file.DestinationName}", nameof(files));
            if (!names.Add(file.DestinationName))
                throw new ArgumentException($"配置先ファイル名が重複しています: {file.DestinationName}", nameof(files));
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // 一時領域の掃除失敗で本来のvalidation/download結果を上書きしない。
            CleanupFailureHandler?.Invoke(path, ex);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CleanupFailureHandler?.Invoke(path, ex);
        }
    }

    /// <summary>異常終了後に旧モデルを戻すため、backupと正式配置先の対応を保持する</summary>
    private sealed record BackupRecoveryManifest(string DestinationDirectory);
}

/// <summary>
/// load validation型モデル取得で必要な1ファイルを表す
/// </summary>
public sealed record ManagedModelDownloadFile(
    Uri SourceUrl,
    string DestinationName,
    long? ExpectedSizeBytes = null);

/// <summary>
/// load validation型モデル取得の進捗を表す
/// </summary>
public sealed record ManagedModelTransactionProgress(
    long BytesReceived,
    long? TotalBytes,
    string? CurrentFileName,
    bool IsValidating)
{
    /// <summary>総容量が既知の場合だけ0～100の進捗率を返す</summary>
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}
