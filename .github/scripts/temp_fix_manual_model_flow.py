from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    raw = p.read_bytes()
    newline = "\r\n" if b"\r\n" in raw else "\n"
    text = raw.decode("utf-8").replace("\r\n", "\n")
    if old not in text:
        raise SystemExit(f"anchor not found: {path}")
    text = text.replace(old, new, 1)
    p.write_bytes(text.replace("\n", newline).encode("utf-8"))


replace_once(
    "src/VoxArchive.Application.Abstractions/ITranscriptionApplicationService.cs",
    "    Task<TranscriptionEnqueueResult> TryEnqueueAsync(string audioFilePath, RecordingOptions recordingOptions, TranscriptionTrigger trigger, CancellationToken cancellationToken = default);\n",
    """    Task<TranscriptionEnqueueResult> TryEnqueueAsync(string audioFilePath, RecordingOptions recordingOptions, TranscriptionTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>
    /// 手動Admissionで不足したモデルについて利用者確認が完了した後、モデル取得と再Admissionを行う
    /// </summary>
    /// <remarks>
    /// UIは確認と進捗表示だけを担当し、download完了待ちと再Queue投入のpolicyはApplicationへ集約する。
    /// 確認後に必要モデルが変化した場合は、未確認の別モデルを暗黙に取得せずMissingModelを返す。
    /// </remarks>
    Task<TranscriptionEnqueueResult> DownloadMissingModelAndRetryManualEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionMissingModelInfo confirmedModel,
        IProgress<TranscriptionModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default);
""",
)

replace_once(
    "src/VoxArchive.Application/TranscriptionApplicationService.cs",
    """    /// <inheritdoc />
    public bool CancelJob(string audioFilePath) => _jobQueue.Cancel(audioFilePath);
""",
    """    /// <inheritdoc />
    public async Task<VoxArchive.Application.Abstractions.TranscriptionEnqueueResult> DownloadMissingModelAndRetryManualEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionMissingModelInfo confirmedModel,
        IProgress<TranscriptionModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(recordingOptions);
        ArgumentNullException.ThrowIfNull(confirmedModel);

        // 確認Dialog表示後にも設定・Queue状態・別downloadが変化し得るため、取得前に必ず再Admissionする。
        // ここで既にenqueue可能ならdownloadを開始せず、その結果をそのまま返す。
        var current = await _jobQueue.TryEnqueueAsync(
            audioFilePath,
            recordingOptions,
            TranscriptionTrigger.Manual,
            cancellationToken);
        if (current.Enqueued || current.MissingModel is null)
        {
            return ToEnqueueResult(current);
        }

        if (!IsSameModel(current.MissingModel, confirmedModel))
        {
            // 利用者が確認していない別モデルへ要求が変わった場合は安全側に倒す。
            // Presentationへ新しいMissingModelを返し、暗黙downloadは行わない。
            return ToEnqueueResult(current);
        }

        var adapter = progress is null
            ? null
            : new Progress<TranscriptionModelTransferProgress>(x =>
                progress.Report(new TranscriptionModelTransferInfo(x.BytesReceived, x.TotalBytes)));
        await _modelManager.InstallAsync(
            ToModelKey(confirmedModel.EngineId, confirmedModel.ModelId),
            force: false,
            adapter,
            cancellationToken);

        // download完了後のreadiness・reservation・immutable snapshotはAdmissionで改めて確定する。
        // WPF側でAdmission手順を複製しないことで、通常実行と再文字起こしのpolicyを一致させる。
        var retried = await _jobQueue.TryEnqueueAsync(
            audioFilePath,
            recordingOptions,
            TranscriptionTrigger.Manual,
            cancellationToken);
        return ToEnqueueResult(retried);
    }

    /// <inheritdoc />
    public bool CancelJob(string audioFilePath) => _jobQueue.Cancel(audioFilePath);
""",
)

replace_once(
    "src/VoxArchive.Application/TranscriptionApplicationService.cs",
    """    private static TranscriptionModelStatusInfo ToStatus(TranscriptionModelPackageState state, bool isReady) => new(state.ToString(), isReady);
    private static EngineId ToEngineId(string value) => new(value);
""",
    """    private static VoxArchive.Application.Abstractions.TranscriptionEnqueueResult ToEnqueueResult(TranscriptionEnqueueResult result)
        => new(result.Enqueued, result.Message, result.MissingModel);

    private static bool IsSameModel(TranscriptionMissingModelInfo left, TranscriptionMissingModelInfo right)
        => string.Equals(left.EngineId, right.EngineId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.ModelId, right.ModelId, StringComparison.OrdinalIgnoreCase);

    private static TranscriptionModelStatusInfo ToStatus(TranscriptionModelPackageState state, bool isReady) => new(state.ToString(), isReady);
    private static EngineId ToEngineId(string value) => new(value);
""",
)

replace_once(
    "src/VoxArchive.Application/TranscriptionApplicationService.cs",
    """        return new VoxArchive.Application.Abstractions.TranscriptionEnqueueResult(
            result.Enqueued,
            result.Message,
            result.MissingModel);
""",
    "        return ToEnqueueResult(result);\n",
)

coordinator = """using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// 手動文字起こしの利用者確認とモデル取得進捗UIをApplication Use Caseへ接続する
/// </summary>
/// <remarks>
/// MissingModelの判定、download完了待ち、再AdmissionはApplicationが所有する。
/// 本クラスは利用者の承認とWindow表示だけを担当し、通常実行と再文字起こしで同じPresentation flowを共有する。
/// </remarks>
public sealed class ManualTranscriptionEnqueueCoordinator(ITranscriptionApplicationService transcriptionService)
{
    /// <summary>
    /// 手動文字起こしを要求し、不足モデルがある場合は確認後にApplicationへ継続を委譲する
    /// </summary>
    public async Task<TranscriptionEnqueueResult> TryEnqueueAsync(
        string audioFilePath,
        RecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(options);

        var result = await transcriptionService.TryEnqueueAsync(
            audioFilePath,
            options,
            TranscriptionTrigger.Manual,
            cancellationToken);
        if (result.Enqueued || result.MissingModel is null)
        {
            return result;
        }

        var missingModel = result.MissingModel;
        var confirmation = ModernDialog.Show(
            $"文字起こしに必要なモデル「{missingModel.DisplayName}」が取得されていません。\nモデルを取得して文字起こしを続行しますか？",
            "文字起こしモデル未取得",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel);
        if (confirmation != System.Windows.MessageBoxResult.OK)
        {
            return new TranscriptionEnqueueResult(
                false,
                "モデル取得がキャンセルされたため文字起こしを開始しませんでした。");
        }

        var progressWindow = new TranscriptionModelDownloadProgressWindow(
            missingModel,
            () => transcriptionService.CancelModelDownload(missingModel.EngineId, missingModel.ModelId))
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        var progress = new Progress<TranscriptionModelTransferInfo>(progressWindow.Report);

        try
        {
            // Application側が再Admissionしてから必要な場合だけdownloadを開始する。
            // すでにモデルがreadyになっていた場合はWindowを一瞬表示しない。
            var continuation = transcriptionService.DownloadMissingModelAndRetryManualEnqueueAsync(
                audioFilePath,
                options,
                missingModel,
                progress,
                cancellationToken);

            if (!continuation.IsCompleted)
            {
                progressWindow.Show();
                var activeDownload = transcriptionService.GetActiveModelDownload();
                if (activeDownload is not null
                    && string.Equals(activeDownload.EngineId, missingModel.EngineId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activeDownload.ModelId, missingModel.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    progressWindow.Report(new TranscriptionModelTransferInfo(
                        activeDownload.BytesReceived,
                        activeDownload.TotalBytes));
                }
            }

            return await continuation;
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionEnqueueResult(
                false,
                "モデル取得がキャンセルされたため文字起こしを開始しませんでした。");
        }
        finally
        {
            progressWindow.CloseAfterCompletion();
        }
    }
}
"""
Path("src/VoxArchive.Wpf/ManualTranscriptionEnqueueCoordinator.cs").write_text(coordinator, encoding="utf-8", newline="")

replace_once(
    "src/VoxArchive.Wpf/App.xaml.cs",
    """                    services.AddVoxArchiveTranscription();

                    services.AddTransient<MainViewModel>(sp =>
""",
    """                    services.AddVoxArchiveTranscription();
                    services.AddSingleton<ManualTranscriptionEnqueueCoordinator>();

                    services.AddTransient<MainViewModel>(sp =>
""",
)

replace_once(
    "src/VoxArchive.Wpf/LibraryViewModel.cs",
    """    private readonly ITranscriptionApplicationService _transcriptionService;
    private readonly Func<RecordingOptions> _optionsProvider;
""",
    """    private readonly ITranscriptionApplicationService _transcriptionService;
    private readonly ManualTranscriptionEnqueueCoordinator _manualTranscriptionCoordinator;
    private readonly Func<RecordingOptions> _optionsProvider;
""",
)
replace_once(
    "src/VoxArchive.Wpf/LibraryViewModel.cs",
    """        RecordingCatalogService catalogService,
        ITranscriptionApplicationService transcriptionService,
        Func<RecordingOptions> optionsProvider,
""",
    """        RecordingCatalogService catalogService,
        ITranscriptionApplicationService transcriptionService,
        ManualTranscriptionEnqueueCoordinator manualTranscriptionCoordinator,
        Func<RecordingOptions> optionsProvider,
""",
)
replace_once(
    "src/VoxArchive.Wpf/LibraryViewModel.cs",
    """        _transcriptionService = transcriptionService;
        _optionsProvider = optionsProvider;
""",
    """        _transcriptionService = transcriptionService;
        _manualTranscriptionCoordinator = manualTranscriptionCoordinator;
        _optionsProvider = optionsProvider;
""",
)
replace_once(
    "src/VoxArchive.Wpf/LibraryViewModel.cs",
    "            var result = await TryEnqueueManualTranscriptionAsync(SelectedItem.FilePath, options);\n",
    "            var result = await _manualTranscriptionCoordinator.TryEnqueueAsync(SelectedItem.FilePath, options);\n",
)

lib = Path("src/VoxArchive.Wpf/LibraryViewModel.cs")
raw = lib.read_bytes()
newline = "\r\n" if b"\r\n" in raw else "\n"
text = raw.decode("utf-8").replace("\r\n", "\n")
start = text.find("    /// <summary>\n    /// 手動文字起こしをAdmissionし、不足モデルがある場合だけ利用者確認と取得UIを挟んで再Admissionする\n")
end = text.find("    private void OnTranscriptionJobCompleted(", start)
if start < 0 or end < 0:
    raise SystemExit("Library manual helper block not found")
text = text[:start] + text[end:]
lib.write_bytes(text.replace("\n", newline).encode("utf-8"))

replace_once(
    "src/VoxArchive.Wpf/LibraryRetranscriptionService.cs",
    """public sealed class LibraryRetranscriptionService(
    ITranscriptionApplicationService transcriptionApplicationService,
    ISettingsService settingsService)
""",
    """public sealed class LibraryRetranscriptionService(
    ITranscriptionApplicationService transcriptionApplicationService,
    ISettingsService settingsService,
    ManualTranscriptionEnqueueCoordinator manualTranscriptionCoordinator)
""",
)
replace_once(
    "src/VoxArchive.Wpf/LibraryRetranscriptionService.cs",
    """        var result = await transcriptionApplicationService.TryEnqueueAsync(
            audioFilePath,
            prepared.Options,
            TranscriptionTrigger.Manual,
            cancellationToken);
""",
    """        // 通常の手動実行と同じPresentation flowを使い、missing-model確認だけが
        // 再文字起こし経路から抜け落ちることを防ぐ。download/retry policyはApplicationが所有する。
        var result = await manualTranscriptionCoordinator.TryEnqueueAsync(
            audioFilePath,
            prepared.Options,
            cancellationToken);
""",
)

# 一時scriptはソース変更と同じcommitで削除し、最終差分に残さない。
Path(__file__).unlink()
