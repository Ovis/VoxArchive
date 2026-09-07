using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using ModelId = VoxArchive.Transcription.Abstractions.TranscriptionModelId;

namespace VoxArchive.Application;

/// <summary>
/// UI設定をQueue投入可能なimmutable文字起こしsnapshotへ解決する
/// </summary>
/// <remarks>
/// Engine選択、settings deserialize、言語解決、validation、execution validation、model readinessを
/// enqueue前に完了させる。Queue workerはこの処理を再実行せず、投入時点で確定したsnapshotだけを使用する。
/// </remarks>
public sealed class TranscriptionJobAdmissionService(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionModelManager modelManager,
    TranscriptionModelUsageTracker usageTracker)
{
    /// <summary>
    /// 現在の録音設定から文字起こしジョブの実行条件を確定する
    /// </summary>
    public async Task<TranscriptionAdmissionResult> AdmitAsync(
        string audioFilePath,
        RecordingOptions recordingOptions,
        TranscriptionTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(recordingOptions);

        var settings = recordingOptions.Transcription;
        var engineId = new VoxArchive.Transcription.Abstractions.TranscriptionEngineId(settings.DefaultEngine);
        TranscriptionEngineRegistration registration;
        try
        {
            registration = engineRegistry.Get(engineId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return TranscriptionAdmissionResult.Rejected($"文字起こしEngine '{engineId}' は利用できません。");
        }

        if (!settings.Engines.TryGetValue(engineId.Value, out var persistedEngineSettings))
        {
            return TranscriptionAdmissionResult.Rejected($"文字起こしEngine '{engineId}' の設定がありません。");
        }

        ITranscriptionEngineOptions engineOptions;
        try
        {
            engineOptions = registration.SettingsProvider.Deserialize(persistedEngineSettings.Settings, persistedEngineSettings.SchemaVersion);
        }
        catch (Exception ex)
        {
            return TranscriptionAdmissionResult.Rejected($"文字起こしEngine設定を読み込めません: {ex.Message}");
        }

        if (registration.LanguageCapability is not null)
        {
            if (!registration.LanguageCapability.Supports(settings.PreferredLanguage))
            {
                return TranscriptionAdmissionResult.Rejected($"Engine '{engineId}' は希望言語 '{settings.PreferredLanguage}' をサポートしていません。");
            }

            engineOptions = registration.LanguageCapability.Resolve(engineOptions, settings.PreferredLanguage);
        }

        var validationErrors = registration.SettingsProvider.Validate(engineOptions);
        if (validationErrors.Count > 0)
        {
            return TranscriptionAdmissionResult.Rejected(FormatValidationErrors(validationErrors));
        }

        if (registration.ExecutionValidator is not null)
        {
            var executionErrors = await registration.ExecutionValidator.ValidateAsync(engineOptions, cancellationToken);
            if (executionErrors.Count > 0)
            {
                return TranscriptionAdmissionResult.Rejected(FormatValidationErrors(executionErrors));
            }
        }

        ModelId? resolvedModelId = null;
        TranscriptionModelUsageReservation? reservation = null;
        try
        {
            if (registration.ModelRequirementResolver is not null)
            {
                var modelId = registration.ModelRequirementResolver.ResolveRequiredModel(engineOptions);
                resolvedModelId = modelId;
                var modelKey = new TranscriptionModelKey(engineId, modelId);

                if (!modelManager.IsReady(modelKey))
                {
                    // 同一モデルを既に取得中なら、Auto/Manualを問わずそのownerへ相乗りして完了を待つ。
                    // 別途取得を開始するとglobal download concurrency=1とowner/waiter共有規則を壊すため行わない。
                    var waited = await modelManager.WaitForActiveDownloadAsync(modelKey, cancellationToken);
                    if (!waited || !modelManager.IsReady(modelKey))
                    {
                        var displayName = modelManager.GetAvailableModels(engineId)
                            .FirstOrDefault(x => x.ModelId == modelId)?.DisplayName ?? modelId.Value;
                        var missing = new TranscriptionMissingModelInfo(engineId.Value, modelId.Value, displayName);
                        return trigger == TranscriptionTrigger.Manual
                            ? TranscriptionAdmissionResult.RequiresModel(missing)
                            : TranscriptionAdmissionResult.Skipped($"文字起こしモデル '{displayName}' が未取得のため自動文字起こしをスキップしました。");
                    }
                }

                // モデル取得完了後にusage reservationを取得する。
                // 未配置状態で先に予約すると、手動取得自身が「使用中」と判定されて開始できなくなるため順序を固定する。
                reservation = usageTracker.Acquire(modelKey);
                if (!modelManager.IsReady(modelKey))
                {
                    reservation.Dispose();
                    reservation = null;
                    var displayName = modelManager.GetAvailableModels(engineId)
                        .FirstOrDefault(x => x.ModelId == modelId)?.DisplayName ?? modelId.Value;
                    var missing = new TranscriptionMissingModelInfo(engineId.Value, modelId.Value, displayName);
                    return trigger == TranscriptionTrigger.Manual
                        ? TranscriptionAdmissionResult.RequiresModel(missing)
                        : TranscriptionAdmissionResult.Skipped($"文字起こしモデル '{displayName}' が未取得のため自動文字起こしをスキップしました。");
                }

                var installation = modelManager.GetInstallation(modelKey);
                engineOptions = registration.ModelRequirementResolver.BindInstallation(engineOptions, installation);
            }

            var priority = trigger == TranscriptionTrigger.AutoAfterRecord ? settings.AutoPriority : settings.ManualPriority;
            var descriptor = new TranscriptionJobDescriptor(audioFilePath, engineId, resolvedModelId, trigger, settings.DiagnosticsLogEnabled);

            // 既存artifact名との互換性が必要なEngineは自身のcapabilityでsuffixを確定する。
            // Queue投入後に設定が変わっても出力先が変化しないようAdmission snapshotへ含める。
            var artifactSuffix = registration.ArtifactNamingCapability?.BuildFileNameSuffix(resolvedModelId);
            var orchestrationRequest = new TranscriptionOrchestrationRequest(
                audioFilePath,
                engineId,
                engineOptions,
                recordingOptions.DefaultSpeakerPlaybackGainDb,
                recordingOptions.DefaultMicPlaybackGainDb,
                new TranscriptionArtifactOptions(
                    resolvedModelId,
                    ToArtifactFormats(settings.OutputFormats),
                    artifactSuffix));

            return TranscriptionAdmissionResult.Accepted(new AdmittedTranscriptionJob(descriptor, orchestrationRequest, priority, reservation));
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private static string FormatValidationErrors(IReadOnlyList<TranscriptionValidationError> errors)
        => string.Join(Environment.NewLine, errors.Select(x => x.Message));

    private static TranscriptionArtifactFormats ToArtifactFormats(TranscriptionOutputFormats formats)
    {
        var result = TranscriptionArtifactFormats.None;
        if (formats.HasFlag(TranscriptionOutputFormats.Txt)) result |= TranscriptionArtifactFormats.Txt;
        if (formats.HasFlag(TranscriptionOutputFormats.Srt)) result |= TranscriptionArtifactFormats.Srt;
        if (formats.HasFlag(TranscriptionOutputFormats.Vtt)) result |= TranscriptionArtifactFormats.Vtt;
        return result;
    }
}

/// <summary>
/// Admission済みジョブとmodel reservationを保持する
/// </summary>
public sealed record AdmittedTranscriptionJob(
    TranscriptionJobDescriptor Descriptor,
    TranscriptionOrchestrationRequest Request,
    TranscriptionPriority Priority,
    TranscriptionModelUsageReservation? ModelReservation) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => ModelReservation?.Dispose();
}

/// <summary>
/// Admissionの成否、skip、手動モデル取得要求を表す
/// </summary>
public sealed record TranscriptionAdmissionResult(
    bool Succeeded,
    string Message,
    AdmittedTranscriptionJob? Job,
    TranscriptionMissingModelInfo? MissingModel)
{
    public static TranscriptionAdmissionResult Accepted(AdmittedTranscriptionJob job) => new(true, string.Empty, job, null);
    public static TranscriptionAdmissionResult Rejected(string message) => new(false, message, null, null);
    public static TranscriptionAdmissionResult Skipped(string message) => new(false, message, null, null);
    public static TranscriptionAdmissionResult RequiresModel(TranscriptionMissingModelInfo model)
        => new(false, $"文字起こしモデル '{model.DisplayName}' の取得が必要です。", null, model);
}
