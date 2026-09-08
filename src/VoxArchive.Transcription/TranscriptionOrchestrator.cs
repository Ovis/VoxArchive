using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine非依存の文字起こしpipelineを順序どおり実行する
/// </summary>
public sealed class TranscriptionOrchestrator(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionAudioPreparationService audioPreparationService,
    ISpeechRegionDetector speechRegionDetector,
    TranscriptionEngineResultValidator resultValidator,
    TranscriptionSpeakerLabelService speakerLabelService,
    TranscriptionArtifactService artifactService,
    TranscriptionDiagnosticWriter diagnosticWriter,
    ILogger<TranscriptionOrchestrator> logger)
{
    /// <summary>
    /// Audio Preparationからartifact確定までの共通pipelineを実行する
    /// </summary>
    public async Task<TranscriptionOrchestrationResult> TranscribeAsync(
        TranscriptionOrchestrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registration = engineRegistry.Get(request.EngineId);
        var engine = registration.Engine;
        var pipelineStopwatch = Stopwatch.StartNew();
        var stageStopwatch = new Stopwatch();
        var failedStage = "audio-preparation";
        var audioPreparationMilliseconds = 0L;
        var vadMilliseconds = 0L;
        var asrMilliseconds = 0L;
        var speakerLabelingMilliseconds = 0L;
        var canonicalAndOutputMilliseconds = 0L;
        IPreparedTranscriptionAudio? preparedAudio = null;
        IReadOnlyList<SpeechRegion>? speechRegions = null;
        TranscriptionDiagnosticSource? diagnosticSource = null;

        try
        {
            LogStage(request, "audio-preparation", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            preparedAudio = await audioPreparationService.PrepareAsync(
                request.SourceRecordingPath,
                request.SpeakerGainDb,
                request.MicrophoneGainDb,
                engine.AudioRequirements,
                cancellationToken);
            stageStopwatch.Stop();
            audioPreparationMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "audio-preparation", "completed", pipelineStopwatch.ElapsedMilliseconds);

            if (request.DiagnosticsEnabled)
            {
                // SHA-256はモデル検証用途ではなく、A/B比較時にPrepared Audioが同一か識別するためだけに計算する。
                // 診断OFFでは追加I/Oを発生させず、通常の文字起こし経路へ影響させない。
                diagnosticSource = await TryBuildDiagnosticSourceAsync(request.SourceRecordingPath, preparedAudio);
            }

            // VADはWhisper/ReazonSpeechで共通の前処理であり、Engine内部で個別実行すると
            // detector選択やfallback結果がEngineごとに分岐するためCommon pipelineで一度だけ確定する。
            failedStage = "vad";
            LogStage(request, "vad", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            speechRegions = await speechRegionDetector.DetectAsync(
                preparedAudio,
                request.SpeechRegionDetectorSettings,
                cancellationToken);
            stageStopwatch.Stop();
            vadMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "vad", "completed", pipelineStopwatch.ElapsedMilliseconds);

            failedStage = "asr";
            LogStage(request, "recognition", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            var engineResult = await engine.TranscribeAsync(
                new TranscriptionEngineRequest(
                    preparedAudio,
                    speechRegions,
                    request.EngineOptions,
                    new TranscriptionEngineExecutionContext(request.DiagnosticsEnabled)),
                cancellationToken);
            stageStopwatch.Stop();
            asrMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "recognition", "completed", pipelineStopwatch.ElapsedMilliseconds);

            failedStage = "canonical-output";
            stageStopwatch.Restart();
            LogStage(request, "validation", "started", pipelineStopwatch.ElapsedMilliseconds);
            resultValidator.Validate(engineResult, preparedAudio.Duration);
            LogStage(request, "validation", "completed", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Stop();
            canonicalAndOutputMilliseconds += stageStopwatch.ElapsedMilliseconds;

            failedStage = "speaker-labeling";
            LogStage(request, "speaker-labeling", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            var labeled = speakerLabelService.Apply(request.SourceRecordingPath, engineResult.Segments, cancellationToken);
            stageStopwatch.Stop();
            speakerLabelingMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "speaker-labeling", "completed", pipelineStopwatch.ElapsedMilliseconds);

            failedStage = "canonical-output";
            var finishedAt = DateTimeOffset.Now;
            LogStage(request, "artifact-write", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            var artifact = await artifactService.WriteAsync(
                request.SourceRecordingPath,
                request.EngineId,
                request.ArtifactOptions,
                engineResult,
                labeled,
                finishedAt,
                cancellationToken);
            stageStopwatch.Stop();
            canonicalAndOutputMilliseconds += stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "artifact-write", "completed", pipelineStopwatch.ElapsedMilliseconds);

            pipelineStopwatch.Stop();
            if (request.DiagnosticsEnabled)
            {
                await WriteDiagnosticAsync(
                    request,
                    diagnosticSource ?? BuildFallbackDiagnosticSource(request.SourceRecordingPath, preparedAudio),
                    speechRegions,
                    finishedAt,
                    status: "success",
                    failedStage: null,
                    exception: null,
                    pipelineStopwatch.ElapsedMilliseconds,
                    audioPreparationMilliseconds,
                    vadMilliseconds,
                    asrMilliseconds,
                    speakerLabelingMilliseconds,
                    canonicalAndOutputMilliseconds);

                logger.LogInformation(
                    "Transcription pipeline completed. File={File}, Engine={Engine}, ElapsedMs={ElapsedMs}, SpeechRegionCount={SpeechRegionCount}, SegmentCount={SegmentCount}, GeneratedFileCount={GeneratedFileCount}",
                    request.SourceRecordingPath,
                    request.EngineId,
                    pipelineStopwatch.ElapsedMilliseconds,
                    speechRegions.Count,
                    engineResult.Segments.Count,
                    artifact.GeneratedFiles.Count);
            }

            return new TranscriptionOrchestrationResult(artifact.DocumentPath, artifact.GeneratedFiles, engineResult.Metadata, finishedAt);
        }
        catch (Exception ex)
        {
            pipelineStopwatch.Stop();
            if (request.DiagnosticsEnabled)
            {
                // 診断生成そのものは本来の例外を上書きしてはならない。
                // Prepared Audio生成前の失敗でも、取得可能なsource情報だけでpartial diagnosticを残す。
                try
                {
                    await WriteDiagnosticAsync(
                        request,
                        diagnosticSource ?? BuildFallbackDiagnosticSource(request.SourceRecordingPath, preparedAudio, engine.AudioRequirements),
                        speechRegions,
                        DateTimeOffset.Now,
                        status: "failed",
                        failedStage,
                        new TranscriptionDiagnosticException(ex.GetType().FullName ?? ex.GetType().Name, ex.Message),
                        pipelineStopwatch.ElapsedMilliseconds,
                        audioPreparationMilliseconds,
                        vadMilliseconds,
                        asrMilliseconds,
                        speakerLabelingMilliseconds,
                        canonicalAndOutputMilliseconds);
                }
                catch (Exception diagnosticException)
                {
                    logger.LogWarning(
                        diagnosticException,
                        "Failed to prepare transcription diagnostic after pipeline failure. File={File}, Stage={Stage}",
                        Path.GetFileName(request.SourceRecordingPath),
                        failedStage);
                }
            }
            throw;
        }
        finally
        {
            if (preparedAudio is not null)
            {
                await preparedAudio.DisposeAsync();
            }
        }
    }

    private async Task<TranscriptionDiagnosticSource> TryBuildDiagnosticSourceAsync(
        string sourceRecordingPath,
        IPreparedTranscriptionAudio preparedAudio)
    {
        string? sha256 = null;
        try
        {
            await using var stream = await preparedAudio.OpenReadAsync(CancellationToken.None);
            var hash = await SHA256.HashDataAsync(stream, CancellationToken.None);
            sha256 = Convert.ToHexString(hash);
        }
        catch (Exception ex)
        {
            // input identity取得失敗だけで文字起こし本体を失敗させない。
            // hash欠落は診断JSONから判別できるため、通常ログへwarningを残して処理は継続する。
            logger.LogWarning(
                ex,
                "Failed to hash prepared transcription audio for diagnostics. File={File}",
                Path.GetFileName(sourceRecordingPath));
        }

        return BuildFallbackDiagnosticSource(sourceRecordingPath, preparedAudio) with
        {
            PreparedAudioSha256 = sha256
        };
    }

    private static TranscriptionDiagnosticSource BuildFallbackDiagnosticSource(
        string sourceRecordingPath,
        IPreparedTranscriptionAudio? preparedAudio,
        TranscriptionAudioRequirements? fallbackFormat = null)
    {
        var format = preparedAudio?.Format ?? fallbackFormat ?? new TranscriptionAudioRequirements(0, 1, TranscriptionSampleFormat.Pcm16);
        long fileSize = 0;
        try
        {
            if (File.Exists(sourceRecordingPath)) fileSize = new FileInfo(sourceRecordingPath).Length;
        }
        catch
        {
            // source metadataは診断補助情報なので、取得不能でも本処理や元の失敗理由を上書きしない。
        }

        return new TranscriptionDiagnosticSource(
            Path.GetFileName(sourceRecordingPath),
            fileSize,
            preparedAudio?.SampleCount ?? 0,
            format.SampleRate,
            PreparedAudioSha256: null);
    }

    private async Task WriteDiagnosticAsync(
        TranscriptionOrchestrationRequest request,
        TranscriptionDiagnosticSource source,
        IReadOnlyList<SpeechRegion>? speechRegions,
        DateTimeOffset timestamp,
        string status,
        string? failedStage,
        TranscriptionDiagnosticException? exception,
        long overallMilliseconds,
        long audioPreparationMilliseconds,
        long vadMilliseconds,
        long asrMilliseconds,
        long speakerLabelingMilliseconds,
        long canonicalAndOutputMilliseconds)
    {
        var snapshot = request.ArtifactOptions.ExecutionSnapshot;
        var document = new TranscriptionDiagnosticDocument
        {
            ApplicationVersion = typeof(TranscriptionOrchestrator).Assembly.GetName().Version?.ToString() ?? "unknown",
            Status = status,
            FailedStage = failedStage,
            Source = source,
            Settings = new TranscriptionDiagnosticSettings
            {
                EngineId = request.EngineId.Value,
                ModelId = request.ArtifactOptions.ModelId?.Value,
                EngineSettingsSchemaVersion = snapshot?.EngineSettingsSchemaVersion,
                EngineSettings = CloneIfDefined(snapshot?.EngineSettings),
                PreferredLanguage = snapshot?.PreferredLanguage,
                VadSettingsSchemaVersion = request.SpeechRegionDetectorSettings.SchemaVersion,
                VadSettings = CloneIfDefined(request.SpeechRegionDetectorSettings.Settings),
                SpeakerGainDb = request.SpeakerGainDb,
                MicrophoneGainDb = request.MicrophoneGainDb
            },
            Vad = speechRegions is null
                ? null
                : new TranscriptionDiagnosticVad
                {
                    Detector = speechRegionDetector.GetType().Name,
                    Settings = CloneIfDefined(request.SpeechRegionDetectorSettings.Settings),
                    SpeechRegions = speechRegions.Select(ToDiagnosticSpeechRegion).ToArray(),
                    FallbackUsed = false,
                    FallbackReason = null,
                    ElapsedMilliseconds = vadMilliseconds
                },
            Timings = new TranscriptionDiagnosticTimings
            {
                OverallMilliseconds = overallMilliseconds,
                AudioPreparationMilliseconds = audioPreparationMilliseconds,
                VadMilliseconds = vadMilliseconds,
                ChunkGenerationMilliseconds = 0,
                AsrMilliseconds = asrMilliseconds,
                SpeakerLabelingMilliseconds = speakerLabelingMilliseconds,
                CanonicalAndOutputMilliseconds = canonicalAndOutputMilliseconds
            },
            Exception = exception
        };

        // 診断書き込みの失敗やJobキャンセルで、本体の成功/失敗結果を変えない。
        // writer自身が通常ログへwarningを残すため、戻り値は本pipelineでは利用しない。
        await diagnosticWriter.TryWriteAsync(request.SourceRecordingPath, timestamp, document, CancellationToken.None);
    }

    private static TranscriptionDiagnosticSpeechRegion ToDiagnosticSpeechRegion(SpeechRegion region)
        => new(
            region.SpeechRegionId,
            region.StartSample,
            region.EndSample,
            region.CoreRanges
                .Select(range => new TranscriptionDiagnosticSampleRange(range.StartSample, range.EndSample))
                .ToArray(),
            region.SourceRawSpeechRegionIds.ToArray());

    private static System.Text.Json.JsonElement? CloneIfDefined(System.Text.Json.JsonElement? element)
        => element is { ValueKind: not System.Text.Json.JsonValueKind.Undefined }
            ? element.Value.Clone()
            : null;

    private void LogStage(TranscriptionOrchestrationRequest request, string stage, string state, long elapsedMilliseconds)
    {
        if (!request.DiagnosticsEnabled) return;
        logger.LogInformation(
            "Transcription pipeline stage. File={File}, Engine={Engine}, Stage={Stage}, State={State}, ElapsedMs={ElapsedMs}",
            request.SourceRecordingPath,
            request.EngineId,
            stage,
            state,
            elapsedMilliseconds);
    }
}

/// <summary>
/// enqueue前に確定済みの実行条件をCommon Orchestratorへ渡す
/// </summary>
public sealed record TranscriptionOrchestrationRequest(
    string SourceRecordingPath,
    TranscriptionEngineId EngineId,
    ITranscriptionEngineOptions EngineOptions,
    double SpeakerGainDb,
    double MicrophoneGainDb,
    TranscriptionArtifactOptions ArtifactOptions,
    bool DiagnosticsEnabled)
{
    /// <summary>
    /// Queue投入時点で固定されたVAD設定を取得する
    /// </summary>
    /// <remarks>
    /// 旧call-siteとの互換性を保つため既定値を持つ。設定UIとの接続時にAdmissionが明示的なsnapshotへ置き換える。
    /// </remarks>
    public SpeechRegionDetectorSettingsSnapshot SpeechRegionDetectorSettings { get; init; } = new(1, default);
}

/// <summary>
/// Common pipelineの完了結果を表す
/// </summary>
public sealed record TranscriptionOrchestrationResult(
    string DocumentPath,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyDictionary<string, object?>? EngineMetadata,
    DateTimeOffset FinishedAt);
