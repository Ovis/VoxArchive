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
    ILogger<TranscriptionOrchestrator> logger,
    TranscriptionEngineResultCanonicalizer? resultCanonicalizer = null)
{
    private readonly TranscriptionEngineResultCanonicalizer _resultCanonicalizer =
        resultCanonicalizer ?? new TranscriptionEngineResultCanonicalizer();

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
        SpeechRegionDetectionDiagnosticTrace? vadTrace = null;
        TranscriptionEngineDiagnosticTrace? engineDiagnostic = null;
        TranscriptionEngineResult? rawEngineResult = null;
        TranscriptionEngineResult? canonicalEngineResult = null;
        IReadOnlyList<SpeakerLabelingDiagnosticTrace>? speakerDiagnostic = null;
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
            if (request.DiagnosticsEnabled && speechRegionDetector is IDiagnosticSpeechRegionDetector diagnosticDetector)
            {
                // traceをdetectorの可変状態へ保存して後から読むと、並列Jobで別Jobの情報を拾う可能性がある。
                // SpeechRegionと同じ呼び出し結果として受け取り、Job単位で対応関係を固定する。
                var detection = await diagnosticDetector.DetectWithDiagnosticsAsync(
                    preparedAudio,
                    request.SpeechRegionDetectorSettings,
                    cancellationToken);
                speechRegions = detection.SpeechRegions;
                vadTrace = detection.Trace;
            }
            else
            {
                speechRegions = await speechRegionDetector.DetectAsync(
                    preparedAudio,
                    request.SpeechRegionDetectorSettings,
                    cancellationToken);
            }
            stageStopwatch.Stop();
            vadMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "vad", "completed", pipelineStopwatch.ElapsedMilliseconds);

            failedStage = "asr";
            LogStage(request, "recognition", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            rawEngineResult = await engine.TranscribeAsync(
                new TranscriptionEngineRequest(
                    preparedAudio,
                    speechRegions,
                    request.EngineOptions,
                    new TranscriptionEngineExecutionContext(request.DiagnosticsEnabled)),
                cancellationToken);
            engineDiagnostic = rawEngineResult.Diagnostics;
            stageStopwatch.Stop();
            asrMilliseconds = stageStopwatch.ElapsedMilliseconds;
            LogStage(request, "recognition", "completed", pipelineStopwatch.ElapsedMilliseconds);

            failedStage = "canonical-output";
            stageStopwatch.Restart();
            canonicalEngineResult = _resultCanonicalizer.Canonicalize(rawEngineResult, preparedAudio);
            LogStage(request, "validation", "started", pipelineStopwatch.ElapsedMilliseconds);
            resultValidator.Validate(canonicalEngineResult, preparedAudio.Duration);
            LogStage(request, "validation", "completed", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Stop();
            canonicalAndOutputMilliseconds += stageStopwatch.ElapsedMilliseconds;

            failedStage = "speaker-labeling";
            LogStage(request, "speaker-labeling", "started", pipelineStopwatch.ElapsedMilliseconds);
            stageStopwatch.Restart();
            IReadOnlyList<LabeledTranscriptionSegment> labeled;
            if (request.DiagnosticsEnabled)
            {
                // 診断値を通常処理とは別に再計算するとラベルと根拠が食い違う可能性があるため、
                // 実際にラベル判定へ使用した同じCH1/CH2エネルギーを同一呼び出しから受け取る。
                var speakerResult = speakerLabelService.ApplyWithDiagnostics(
                    request.SourceRecordingPath,
                    canonicalEngineResult.Segments,
                    cancellationToken);
                labeled = speakerResult.Segments;
                speakerDiagnostic = speakerResult.Traces;
            }
            else
            {
                labeled = speakerLabelService.Apply(
                    request.SourceRecordingPath,
                    canonicalEngineResult.Segments,
                    cancellationToken);
            }
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
                canonicalEngineResult,
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
                    vadTrace,
                    engineDiagnostic,
                    canonicalEngineResult,
                    speakerDiagnostic,
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
                    canonicalEngineResult.Segments.Count,
                    artifact.GeneratedFiles.Count);
            }

            return new TranscriptionOrchestrationResult(
                artifact.DocumentPath,
                artifact.GeneratedFiles,
                canonicalEngineResult.Metadata,
                finishedAt);
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
                        vadTrace,
                        engineDiagnostic,
                        canonicalEngineResult,
                        speakerDiagnostic,
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
        SpeechRegionDetectionDiagnosticTrace? vadTrace,
        TranscriptionEngineDiagnosticTrace? engineDiagnostic,
        TranscriptionEngineResult? canonicalEngineResult,
        IReadOnlyList<SpeakerLabelingDiagnosticTrace>? speakerDiagnostic,
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
                    Detector = vadTrace?.Detector ?? speechRegionDetector.GetType().Name,
                    Settings = CloneIfDefined(request.SpeechRegionDetectorSettings.Settings),
                    RawRegions = vadTrace?.RawRegions
                        .Select(range => new TranscriptionDiagnosticSpeechRange(
                            range.RawSpeechRegionId,
                            range.StartSample,
                            range.EndSample))
                        .ToArray() ?? [],
                    SpeechRegions = speechRegions.Select(ToDiagnosticSpeechRegion).ToArray(),
                    FallbackUsed = vadTrace?.FallbackUsed ?? false,
                    FallbackReason = vadTrace?.FallbackReason,
                    ElapsedMilliseconds = vadMilliseconds
                },
            RecognitionChunks = engineDiagnostic?.RecognitionChunks
                .Select(trace => new TranscriptionDiagnosticRecognitionChunk
                {
                    RecognitionChunkId = trace.RecognitionChunkId,
                    SpeechRegionId = trace.SpeechRegionId,
                    StartSample = trace.StartSample,
                    EndSample = trace.EndSample,
                    SplitReason = trace.SplitReason,
                    SplitDetails = CloneIfDefined(trace.SplitDetails),
                    ElapsedMilliseconds = 0
                })
                .ToArray() ?? [],
            AsrResults = engineDiagnostic?.AsrResults?
                .Select(trace =>
                {
                    // canonical text規則はartifactと同じくCommon側で一元化する。
                    // 1chunkから複数raw segmentが返るWhisperでも、各raw結果を独立して比較できるようchunk単位では集約しない。
                    var canonicalText = CanonicalizeDiagnosticText(trace.RawText);
                    var discarded = trace.Discarded || canonicalText is null;
                    return new TranscriptionDiagnosticAsrResult
                    {
                        RecognitionChunkId = trace.RecognitionChunkId,
                        RawText = trace.RawText,
                        CanonicalText = canonicalText,
                        TimestampTrace = CloneIfDefined(trace.TimestampTrace),
                        Discarded = discarded,
                        DiscardReason = trace.DiscardReason
                            ?? (discarded ? "canonical-discarded" : null)
                    };
                })
                .ToArray() ?? [],
            SpeakerResults = speakerDiagnostic?
                .Select(trace => new TranscriptionDiagnosticSpeakerResult
                {
                    RecognitionChunkId = trace.RecognitionChunkId,
                    SpeakerLabel = trace.SpeakerLabel,
                    SpeakerChannelEnergy = trace.SpeakerChannelEnergy,
                    MicrophoneChannelEnergy = trace.MicrophoneChannelEnergy
                })
                .ToArray() ?? [],
            Timings = new TranscriptionDiagnosticTimings
            {
                OverallMilliseconds = overallMilliseconds,
                AudioPreparationMilliseconds = audioPreparationMilliseconds,
                VadMilliseconds = vadMilliseconds,
                // Engine内部でchunkingとRecognizer呼び出しを分離計測できる場合はその値を採用する。
                // 診断traceを持たないEngineでは従来どおりrecognition stage全体をASR時間として扱う。
                ChunkGenerationMilliseconds = engineDiagnostic?.ChunkGenerationMilliseconds ?? 0,
                AsrMilliseconds = engineDiagnostic?.AsrMilliseconds ?? asrMilliseconds,
                SpeakerLabelingMilliseconds = speakerLabelingMilliseconds,
                CanonicalAndOutputMilliseconds = canonicalAndOutputMilliseconds
            },
            Exception = exception
        };

        // 診断書き込みの失敗やJobキャンセルで、本体の成功/失敗結果を変えない。
        // writer自身が通常ログへwarningを残すため、戻り値は本pipelineでは利用しない。
        await diagnosticWriter.TryWriteAsync(request.SourceRecordingPath, timestamp, document, CancellationToken.None);
    }

    private static string? CanonicalizeDiagnosticText(string? rawText)
    {
        var text = rawText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
