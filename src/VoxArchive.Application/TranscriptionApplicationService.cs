using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using EngineId = VoxArchive.Transcription.Abstractions.TranscriptionEngineId;
using ModelId = VoxArchive.Transcription.Abstractions.TranscriptionModelId;

namespace VoxArchive.Application;

/// <summary>
/// WPF向けFacadeとして文字起こしQueue、モデル管理、Engine診断をEngine非依存DTOへ投影する
/// </summary>
public sealed class TranscriptionApplicationService : ITranscriptionApplicationService, IDisposable
{
    private readonly TranscriptionJobQueue _jobQueue;
    private readonly TranscriptionModelManager _modelManager;
    private readonly TranscriptionEngineRegistry _engineRegistry;

    public TranscriptionApplicationService(TranscriptionJobQueue jobQueue, TranscriptionModelManager modelManager, TranscriptionEngineRegistry engineRegistry)
    {
        _jobQueue = jobQueue;
        _modelManager = modelManager;
        _engineRegistry = engineRegistry;
        _jobQueue.JobCompleted += OnJobCompleted;
        _jobQueue.JobStateChanged += OnJobStateChanged;
        _modelManager.StateChanged += OnModelStateChanged;
    }

    public event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;
    public event EventHandler<TranscriptionJobStateChangedEventArgs>? JobStateChanged;
    public event EventHandler? ModelStateChanged;

    /// <inheritdoc />
    public async Task<VoxArchive.Application.Abstractions.TranscriptionEnqueueResult> TryEnqueueAsync(string audioFilePath, RecordingOptions recordingOptions, TranscriptionTrigger trigger, CancellationToken cancellationToken = default)
    {
        var result = await _jobQueue.TryEnqueueAsync(audioFilePath, recordingOptions, trigger, cancellationToken);
        return new VoxArchive.Application.Abstractions.TranscriptionEnqueueResult(result.Enqueued, result.Message);
    }

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
        var path = TranscriptionArtifactService.BuildDocumentPath(audioFilePath, engineId, modelId);
        return File.Exists(path) ? path : null;
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
