using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

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
            engineOptions = registration.SettingsProvider.Deserialize(
                persistedEngineSettings.Settings,
                persistedEngineSettings.SchemaVersion);
        }
        catch (Exception ex)
        {
            return TranscriptionAdmissionResult.Rejected($"文字起こしEngine設定を読み込めません: {ex.Message}");
        }

        if (registration.LanguageCapability is not null)
        {
            if (!registration.LanguageCapability.Supports(settings.PreferredLanguage))
            {
                return TranscriptionAdmissionResult.Rejected(
                    $"Engine '{engineId}' は希望言語 '{settings.PreferredLanguage}' をサポートしていません。");
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

        TranscriptionModelKey? modelKey = null;
        TranscriptionModelUsageReservation? reservation = null;
        try
        {
            if (registration.ModelRequirementResolver is not null)
            {
                var modelId = registration.ModelRequirementResolver.ResolveRequiredModel(engineOptions);
                modelKey = new TranscriptionModelKey(engineId, modelId);

                // readiness確認からworker完了までモデルを保護する。
                // この順序により、確認直後に設定画面からモデルを削除されるTOCTOUを防止する。
                reservation = usageTracker.Acquire(modelKey);

                if (!modelManager.IsReady(modelKey))
                {
                    // 同一モデルの取得が既に進行中ならそのowner Taskだけを待つ。
                    // Job Admission自身は暗黙downloadを開始せず、取得されていなければ利用者へ明示的に返す。
                    var waited = await modelManager.WaitForActiveDownloadAsync(modelKey, cancellationToken);
                    if (!waited || !modelManager.IsReady(modelKey))
                    {
                        return RejectAndRelease(
                            reservation,
                            $"文字起こしモデル '{modelId}' が未配置または不完全です。設定画面からモデルを取得してください。");
                    }
                }

                var installation = modelManager.GetInstallation(modelKey);
                engineOptions = registration.ModelRequirementResolver.BindInstallation(engineOptions, installation);
            }

            var priority = trigger == TranscriptionTrigger.AutoAfterRecord
                ? settings.AutoPriority
                : settings.ManualPriority;
            var artifactFormats = ToArtifactFormats(settings.OutputFormats);
            var descriptor = new TranscriptionJobDescriptor(
                audioFilePath,
                engineId,
                modelKey?.ModelId,
                trigger,
                settings.DiagnosticsLogEnabled);
            var orchestrationRequest = new TranscriptionOrchestrationRequest(
                audioFilePath,
                engineId,
                engineOptions,
                recordingOptions.DefaultSpeakerPlaybackGainDb,
                recordingOptions.DefaultMicPlaybackGainDb,
                new TranscriptionArtifactOptions(modelKey?.ModelId, artifactFormats));

            return TranscriptionAdmissionResult.Accepted(
                new AdmittedTranscriptionJob(
                    descriptor,
                    orchestrationRequest,
                    priority,
                    settings.ToastNotificationEnabled,
                    reservation));
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private static TranscriptionAdmissionResult RejectAndRelease(
        TranscriptionModelUsageReservation reservation,
        string message)
    {
        reservation.Dispose();
        return TranscriptionAdmissionResult.Rejected(message);
    }

    private static string FormatValidationErrors(IReadOnlyList<TranscriptionValidationError> errors)
        => string.Join(Environment.NewLine, errors.Select(x => x.Message));

    private static TranscriptionArtifactFormats ToArtifactFormats(TranscriptionOutputFormats formats)
    {
        var result = TranscriptionArtifactFormats.None;
        if (formats.HasFlag(TranscriptionOutputFormats.Txt)) result |= TranscriptionArtifactFormats.Txt;
        if (formats.HasFlag(TranscriptionOutputFormats.Srt)) result |= TranscriptionArtifactFormats.Srt;
        if (formats.HasFlag(TranscriptionOutputFormats.Vtt)) result |= TranscriptionArtifactFormats.Vtt;

        // canonical JSONはCommon ArtifactServiceが常に生成するため、旧Json flagは派生形式へ変換しない。
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
    bool ToastNotificationEnabled,
    TranscriptionModelUsageReservation? ModelReservation) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => ModelReservation?.Dispose();
}

/// <summary>
/// Admissionの成否を表す
/// </summary>
public sealed record TranscriptionAdmissionResult(
    bool Succeeded,
    string Message,
    AdmittedTranscriptionJob? Job)
{
    public static TranscriptionAdmissionResult Accepted(AdmittedTranscriptionJob job)
        => new(true, string.Empty, job);

    public static TranscriptionAdmissionResult Rejected(string message)
        => new(false, message, null);
}
