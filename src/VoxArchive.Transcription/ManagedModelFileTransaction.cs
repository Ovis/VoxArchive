namespace VoxArchive.Transcription;

/// <summary>
/// モデル固有形式を知らず、download・staging・validation・atomic commitの安全なライフサイクルだけを提供する
/// </summary>
/// <remarks>
/// Silero/ReazonSpeechはSHA-256ではなく実際のnative load成功を利用可能条件とするため、
/// 検証内容は呼び出し側へ委譲する。既存の正常モデルはvalidation成功まで一切置き換えない。
/// </remarks>
public sealed class ManagedModelFileTransaction(HttpClient httpClient)
{
    private const int CopyBufferSize = 81920;

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
    /// <param name="cancellationToken">downloadは即時キャンセルし、validation中は終了後にcommitを抑止する</param>
    public async Task<string> DownloadValidateCommitAsync(
        IReadOnlyList<ManagedModelDownloadFile> files,
        string destinationDirectory,
        string temporaryRootDirectory,
        Func<string, Task> validateStagingAsync,
        IProgress<ManagedModelTransactionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRootDirectory);
        ArgumentNullException.ThrowIfNull(validateStagingAsync);
        if (files.Count == 0) throw new ArgumentException("モデル取得対象がありません。", nameof(files));
        ValidateFiles(files);

        Directory.CreateDirectory(temporaryRootDirectory);
        var stagingDirectory = Path.Combine(temporaryRootDirectory, $"download-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(temporaryRootDirectory, $"backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        long transferred = 0;
        var totalBytes = files.All(x => x.ExpectedSizeBytes.HasValue)
            ? files.Sum(x => x.ExpectedSizeBytes!.Value)
            : (long?)null;

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

            // 既存正常モデルは新stagingのvalidation成功まで残す。確定時だけ同一ファイルシステム上でrenameする。
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

            TryDeleteDirectory(backupDirectory);
            return destinationDirectory;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
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
