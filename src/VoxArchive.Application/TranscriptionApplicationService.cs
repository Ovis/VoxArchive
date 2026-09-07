using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using EngineId = VoxArchive.Transcription.Abstractions.TranscriptionEngineId;
using ModelId = VoxArchive.Transcription.Abstractions.TranscriptionModelId;

namespace VoxArchive.Application;

/// <summary>
/// WPF向けFacadeとして文字起こしQueue、結果管理、モデル管理、Engine診断をEngine非依存DTOへ投影する
/// </summary>
public sealed class TranscriptionApplicationService : ITranscriptionApplicationService, IDisposable
{
    private readonly TranscriptionJobQueue _jobQueue;
    private readonly TranscriptionModelManager _modelManager;
    private readonly TranscriptionEngineRegistry _engineRegistry;
    private readonly TranscriptionDocumentStore _documentStore;
    private readonly TranscriptionExportService _exportService;
    public TranscriptionApplicationService(
        TranscriptionJobQueue jobQueue,
        TranscriptionModelManager modelManager,
        TranscriptionEngineRegistry engineRegistry,
        TranscriptionDocumentStore documentStore,
        TranscriptionExportService exportService)
    {
        _jobQueue = jobQueue;
        _modelManager = modelManager;
        _engineRegistry = engineRegistry;
        _documentStore = documentStore;
        _exportService = exportService;
        _jobQueue.JobCompleted += OnJobCompleted;
        _jobQueue.JobStateChanged += OnJobStateChanged;
        _modelManager.StateChanged += OnModelStateChanged;
    }

    public event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;
    public event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;
    public event EventHandler? ModelStateChanged;

    /// <inheritdoc />
    public async Task<VoxArchive.Application.Abstractions.TranscriptionEnqueueResult> TryEnqueueAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        var result = await _jobQueue.TryEnqueueAsync(audioFilePath, recordingOptions, trigger, cancellationToken);

        // 利用者への確認やWindow表示はPresentation責務なので、ApplicationからUIを逆呼び出ししない。
        // Admissionが不足モデルを返した場合は構造化したままWPFへ返し、取得Use Caseは明示的なInstallModelAsyncで実行する。
        return new VoxArchive.Application.Abstractions.TranscriptionEnqueueResult(
            result.Enqueued,
            result.Message,
            result.MissingModel);
    }

    /// <inheritdoc />
    public bool CancelJob(string audioFilePath) => _jobQueue.Cancel(audioFilePath);

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionJobStateInfo> GetJobStates()
        => _jobQueue.GetStateSnapshot().Select(x => new TranscriptionJobStateInfo(x.AudioFilePath, x.State)).ToArray();

    /// <inheritdoc />
    public string? FindCanonicalResultPath(string audioFilePath, RecordingOptions recordingOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(recordingOptions);

        var engineId = ToEngineId(recordingOptions.Transcription.DefaultEngine);
        var registration = _engineRegistry.Get(engineId);
        if (!recordingOptions.Transcription.Engines.TryGetValue(engineId.Value, out var persisted)) return null;

        var options = registration.SettingsProvider.Deserialize(persisted.Settings, persisted.SchemaVersion);
        var modelId = registration.ModelRequirementResolver?.ResolveRequiredModel(options);
        var artifactSuffix = registration.ArtifactNamingCapability?.BuildFileNameSuffix(modelId);
        var path = TranscriptionArtifactService.BuildDocumentPath(audioFilePath, engineId, modelId, artifactSuffix);
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranscriptionResultInfo>> DiscoverResultsAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        if (!Directory.Exists(directory)) return Array.Empty<TranscriptionResultInfo>();

        var prefix = Path.GetFileNameWithoutExtension(audioFilePath) + "-";
        var sourceFileName = Path.GetFileName(audioFilePath);
        var results = new List<TranscriptionResultInfo>();

        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await _documentStore.LoadAsync(path, cancellationToken);
                if (!string.Equals(document.SourceFileName, sourceFileName, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new TranscriptionResultInfo(path, document.EngineId, document.ModelId, document.CreatedAt));
            }
            catch (InvalidDataException)
            {
                // 未公開の旧canonical schemaはmigration対象外なのでLibrary一覧にも混在させない。
            }
            catch (JsonException)
            {
                // 同じprefixを持つ別用途JSONや破損ファイルでLibrary全体の一覧取得を失敗させない。
            }
        }

        return results.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    /// <inheritdoc />
    public async Task<TranscriptionResultDocumentInfo> LoadResultAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        var document = await _documentStore.LoadAsync(documentPath, cancellationToken);
        return new TranscriptionResultDocumentInfo(
            documentPath,
            document.SourceFileName,
            document.EngineId,
            document.ModelId,
            document.CreatedAt,
            document.Segments.Select(x => new TranscriptionResultSegmentInfo(x.Start, x.End, x.Text, x.Speaker)).ToArray());
    }

    /// <inheritdoc />
    public async Task<TranscriptionRetranscriptionPreparation> PrepareRetranscriptionAsync(
        string documentPath,
        RecordingOptions currentOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentOptions);
        var document = await _documentStore.LoadAsync(documentPath, cancellationToken);
        var engineId = ToEngineId(document.EngineId);
        var registration = _engineRegistry.Get(engineId);

        if (!currentOptions.Transcription.Engines.TryGetValue(engineId.Value, out var currentPersisted))
        {
            throw new InvalidOperationException($"再文字起こし対象Engine '{engineId}' の現在設定がありません。");
        }

        ITranscriptionEngineOptions engineOptions;
        var preferredLanguage = currentOptions.Transcription.PreferredLanguage;
        var usedFallback = false;

        if (document.ExecutionSnapshot is { } snapshot)
        {
            // 新canonicalでは当時のEngine設定をopaque snapshotとして保持しているため、
            // 現在設定へ依存せず当時の要求条件を復元する。
            engineOptions = registration.SettingsProvider.Deserialize(
                snapshot.EngineSettings,
                snapshot.EngineSettingsSchemaVersion);
            preferredLanguage = snapshot.PreferredLanguage;
        }
        else
        {
            // development中の旧canonicalには実行snapshotがないため現在設定で補完する。
            // migration自体は行わず、再実行時だけfallbackしたことをUIへ明示する。
            engineOptions = registration.SettingsProvider.Deserialize(currentPersisted.Settings, currentPersisted.SchemaVersion);
            usedFallback = true;
        }

        if (registration.LanguageCapability is not null)
        {
            if (!registration.LanguageCapability.Supports(preferredLanguage))
            {
                throw new InvalidOperationException($"Engine '{engineId}' は保存済み希望言語 '{preferredLanguage}' をサポートしていません。");
            }
            engineOptions = registration.LanguageCapability.Resolve(engineOptions, preferredLanguage);
        }

        if (!string.IsNullOrWhiteSpace(document.ModelId))
        {
            if (registration.ModelRequirementResolver is null)
            {
                throw new InvalidOperationException($"Engine '{engineId}' は保存済みModel IDを再適用できません。");
            }
            engineOptions = registration.ModelRequirementResolver.SelectModel(engineOptions, new ModelId(document.ModelId));
        }
        else if (registration.ModelRequirementResolver is not null)
        {
            // モデル利用Engineなのに結果へModel IDがない場合だけ、snapshot/current settings側のモデルを使う。
            usedFallback = true;
        }

        var validationErrors = registration.SettingsProvider.Validate(engineOptions);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors.Select(x => x.Message)));
        }

        var engines = new Dictionary<string, TranscriptionEngineSettings>(currentOptions.Transcription.Engines, StringComparer.OrdinalIgnoreCase)
        {
            [engineId.Value] = new TranscriptionEngineSettings
            {
                SchemaVersion = document.ExecutionSnapshot?.EngineSettingsSchemaVersion ?? currentPersisted.SchemaVersion,
                Settings = registration.SettingsProvider.Serialize(engineOptions)
            }
        };

        var transcription = currentOptions.Transcription with
        {
            DefaultEngine = engineId.Value,
            PreferredLanguage = preferredLanguage,
            Engines = engines
        };

        return new TranscriptionRetranscriptionPreparation(
            currentOptions with { Transcription = transcription },
            usedFallback);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExportResultAsync(
        string documentPath,
        TranscriptionOutputFormats formats,
        CancellationToken cancellationToken = default)
    {
        var document = await _documentStore.LoadAsync(documentPath, cancellationToken);
        return await _exportService.WriteDerivedAsync(documentPath, document, ToArtifactFormats(formats), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteResultAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(documentPath)) File.Delete(documentPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionModelInfo> GetAvailableModels(string engineId)
        => _modelManager.GetAvailableModels(ToEngineId(engineId)).Select(x => new TranscriptionModelInfo(x.ModelId.Value, x.DisplayName)).ToArray();

    /// <inheritdoc />
    public TranscriptionModelStatusInfo InspectModel(string engineId, string modelId)
    {
        var key = ToModelKey(engineId, modelId);
        var inspection = _modelManager.Inspect(key, TranscriptionModelInspectionLevel.Existence);
        return ToStatus(inspection.State, _modelManager.IsReady(key));
    }

    /// <inheritdoc />
    public Task<TranscriptionModelStatusInfo> ReverifyModelAsync(string engineId, string modelId, CancellationToken cancellationToken = default)
    {
        var key = ToModelKey(engineId, modelId);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = _modelManager.Reverify(key);
            return ToStatus(inspection.State, inspection.State == TranscriptionModelPackageState.Installed);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task InstallModelAsync(string engineId, string modelId, bool force, IProgress<TranscriptionModelTransferInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        var adapter = progress is null ? null : new Progress<TranscriptionModelTransferProgress>(x => progress.Report(new TranscriptionModelTransferInfo(x.BytesReceived, x.TotalBytes)));
        await _modelManager.InstallAsync(ToModelKey(engineId, modelId), force, adapter, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteModelAsync(string engineId, string modelId, CancellationToken cancellationToken = default)
        => _modelManager.DeleteAsync(ToModelKey(engineId, modelId), cancellationToken);

    /// <inheritdoc />
    public bool IsModelProtected(string engineId, string modelId) => _modelManager.IsInUse(ToModelKey(engineId, modelId));

    /// <inheritdoc />
    public TranscriptionModelDownloadInfo? GetActiveModelDownload()
    {
        var active = _modelManager.GetActiveDownload();
        return active is null ? null : new TranscriptionModelDownloadInfo(active.Key.EngineId.Value, active.Key.ModelId.Value, active.ModelDisplayName, active.BytesReceived, active.TotalBytes, active.WaiterCount, active.IsCancelling);
    }

    /// <inheritdoc />
    public bool CancelModelDownload(string engineId, string modelId) => _modelManager.CancelActiveDownload(ToModelKey(engineId, modelId));

    /// <inheritdoc />
    public Task CancelActiveModelDownloadAndWaitAsync() => _modelManager.CancelActiveDownloadAndWaitAsync();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranscriptionDiagnosticInfo>> DiagnoseEngineAsync(string engineId, CancellationToken cancellationToken = default)
    {
        var registration = _engineRegistry.Get(ToEngineId(engineId));
        if (registration.Diagnostics is null) return Array.Empty<TranscriptionDiagnosticInfo>();
        var items = await registration.Diagnostics.DiagnoseAsync(cancellationToken);
        return items.Select(x => new TranscriptionDiagnosticInfo(x.Code, x.Message, x.Severity switch
        {
            TranscriptionDiagnosticSeverity.Warning => TranscriptionDiagnosticLevel.Warning,
            TranscriptionDiagnosticSeverity.Error => TranscriptionDiagnosticLevel.Error,
            _ => TranscriptionDiagnosticLevel.Information,
        })).ToArray();
    }

    private static TranscriptionArtifactFormats ToArtifactFormats(TranscriptionOutputFormats formats)
    {
        var result = TranscriptionArtifactFormats.None;
        if (formats.HasFlag(TranscriptionOutputFormats.Txt)) result |= TranscriptionArtifactFormats.Txt;
        if (formats.HasFlag(TranscriptionOutputFormats.Srt)) result |= TranscriptionArtifactFormats.Srt;
        if (formats.HasFlag(TranscriptionOutputFormats.Vtt)) result |= TranscriptionArtifactFormats.Vtt;
        return result;
    }

    private static TranscriptionModelStatusInfo ToStatus(TranscriptionModelPackageState state, bool isReady) => new(state.ToString(), isReady);
    private static EngineId ToEngineId(string value) => new(value);
    private static TranscriptionModelKey ToModelKey(string engineId, string modelId) => new(new EngineId(engineId), new ModelId(modelId));
    private void OnJobCompleted(object? sender, TranscriptionJobCompletedEventArgs e) => JobCompleted?.Invoke(this, e);
    private void OnJobStateChanged(object? sender, TranscriptionJobStateChangedEventArgs e) => JobStateChanged?.Invoke(this, e);
    private void OnModelStateChanged(object? sender, EventArgs e) => ModelStateChanged?.Invoke(this, e);

    /// <inheritdoc />
    public void Dispose()
    {
        _jobQueue.JobCompleted -= OnJobCompleted;
        _jobQueue.JobStateChanged -= OnJobStateChanged;
        _modelManager.StateChanged -= OnModelStateChanged;
    }
}
