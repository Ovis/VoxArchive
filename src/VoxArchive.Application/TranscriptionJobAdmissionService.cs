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
        if (!settings.Enabled)
        {
            // Presentation側の事前チェックだけに依存すると、新しい呼び出し経路を追加した際に
            // 無効設定のままQueueへ到達できるため、ApplicationのAdmission境界でも実行可否を保証する。
            return TranscriptionAdmissionResult.Rejected("文字起こし機能が無効です。設定画面で有効化してください。");
        }

        var engineId = new TranscriptionEngineId(settings.DefaultEngine);
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

            // 再文字起こしで当時の要求条件を復元できるよう、Engine固有settingsはopaque JSONのまま保存する。
            // 物理モデルパスなどBindInstallation後の実行時情報ではなく、利用者が保存した論理設定をsnapshot化する。
            var executionSnapshot = new TranscriptionExecutionSnapshot(
                persistedEngineSettings.SchemaVersion,
                persistedEngineSettings.Settings.Clone(),
                settings.PreferredLanguage);

            var orchestrationRequest = new TranscriptionOrchestrationRequest(
                audioFilePath,
                engineId,
                engineOptions,
                recordingOptions.DefaultSpeakerPlaybackGainDb,
                recordingOptions.DefaultMicPlaybackGainDb,
                new TranscriptionArtifactOptions(
                    resolvedModelId,
                    ToArtifactFormats(settings.OutputFormats),
                    artifactSuffix,
                    executionSnapshot),
                settings.DiagnosticsLogEnabled);

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
/// Queue投入前のAdmission判定種別を表す
/// </summary>
public enum TranscriptionAdmissionOutcome
{
    Accepted = 0,
    Rejected = 1,
    Skipped = 2,
    RequiresModel = 3,
}

/// <summary>
/// Admissionの判定種別、説明、確定済みJob、手動モデル取得要求を表す
/// </summary>
/// <remarks>
/// SkippedはQueueへ投入されるJobのterminal outcomeではなく、Auto実行などを投入前に正常に見送った結果として扱う。
/// 文字列メッセージの解析に依存せず、呼び出し側がexpected skipとvalidation rejectionを区別できるようにする。
/// </remarks>
public sealed record TranscriptionAdmissionResult(
    TranscriptionAdmissionOutcome Outcome,
    string Message,
    AdmittedTranscriptionJob? Job,
    TranscriptionMissingModelInfo? MissingModel)
{
    /// <summary>Queueへ投入可能なAdmission結果かどうかを返す</summary>
    public bool Succeeded => Outcome == TranscriptionAdmissionOutcome.Accepted;

    /// <summary>Admission済みJobを返す</summary>
    public static TranscriptionAdmissionResult Accepted(AdmittedTranscriptionJob job)
        => new(TranscriptionAdmissionOutcome.Accepted, string.Empty, job, null);

    /// <summary>設定不正などにより実行不能な結果を返す</summary>
    public static TranscriptionAdmissionResult Rejected(string message)
        => new(TranscriptionAdmissionOutcome.Rejected, message, null, null);

    /// <summary>Auto policyなどによりQueue投入を正常に見送った結果を返す</summary>
    public static TranscriptionAdmissionResult Skipped(string message)
        => new(TranscriptionAdmissionOutcome.Skipped, message, null, null);

    /// <summary>手動実行を続行するためモデル取得が必要な結果を返す</summary>
    public static TranscriptionAdmissionResult RequiresModel(TranscriptionMissingModelInfo model)
        => new(
            TranscriptionAdmissionOutcome.RequiresModel,
            $"文字起こしモデル '{model.DisplayName}' の取得が必要です。",
            null,
            model);
}
