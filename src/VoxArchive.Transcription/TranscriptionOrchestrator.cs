using System.Diagnostics;
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

        LogStage(request, "audio-preparation", "started", pipelineStopwatch.ElapsedMilliseconds);
        await using var preparedAudio = await audioPreparationService.PrepareAsync(
            request.SourceRecordingPath,
            request.SpeakerGainDb,
            request.MicrophoneGainDb,
            engine.AudioRequirements,
            cancellationToken);
        LogStage(request, "audio-preparation", "completed", pipelineStopwatch.ElapsedMilliseconds);

        // VADはWhisper/ReazonSpeechで共通の前処理であり、Engine内部で個別実行すると
        // detector選択やfallback結果がEngineごとに分岐するためCommon pipelineで一度だけ確定する。
        LogStage(request, "vad", "started", pipelineStopwatch.ElapsedMilliseconds);
        var speechRegions = await speechRegionDetector.DetectAsync(preparedAudio, cancellationToken);
        LogStage(request, "vad", "completed", pipelineStopwatch.ElapsedMilliseconds);

        // Prepared Audioの所有権はCommon pipelineにある。EngineはborrowするだけでDisposeしないため、
        // recognition後のvalidator/post-processが終わるまで同じ音声を安全に再利用できる。
        LogStage(request, "recognition", "started", pipelineStopwatch.ElapsedMilliseconds);
        var engineResult = await engine.TranscribeAsync(
            new TranscriptionEngineRequest(
                preparedAudio,
                speechRegions,
                request.EngineOptions,
                new TranscriptionEngineExecutionContext(request.DiagnosticsEnabled)),
            cancellationToken);
        LogStage(request, "recognition", "completed", pipelineStopwatch.ElapsedMilliseconds);

        LogStage(request, "validation", "started", pipelineStopwatch.ElapsedMilliseconds);
        resultValidator.Validate(engineResult, preparedAudio.Duration);
        LogStage(request, "validation", "completed", pipelineStopwatch.ElapsedMilliseconds);

        LogStage(request, "speaker-labeling", "started", pipelineStopwatch.ElapsedMilliseconds);
        var labeled = speakerLabelService.Apply(
            request.SourceRecordingPath,
            engineResult.Segments,
            cancellationToken);
        LogStage(request, "speaker-labeling", "completed", pipelineStopwatch.ElapsedMilliseconds);

        var finishedAt = DateTimeOffset.Now;
        LogStage(request, "artifact-write", "started", pipelineStopwatch.ElapsedMilliseconds);
        var artifact = await artifactService.WriteAsync(
            request.SourceRecordingPath,
            request.EngineId,
            request.ArtifactOptions,
            engineResult,
            labeled,
            finishedAt,
            cancellationToken);
        LogStage(request, "artifact-write", "completed", pipelineStopwatch.ElapsedMilliseconds);

        if (request.DiagnosticsEnabled)
        {
            logger.LogInformation(
                "Transcription pipeline completed. File={File}, Engine={Engine}, ElapsedMs={ElapsedMs}, SpeechRegionCount={SpeechRegionCount}, SegmentCount={SegmentCount}, GeneratedFileCount={GeneratedFileCount}",
                request.SourceRecordingPath,
                request.EngineId,
                pipelineStopwatch.ElapsedMilliseconds,
                speechRegions.Count,
                engineResult.Segments.Count,
                artifact.GeneratedFiles.Count);
        }

        return new TranscriptionOrchestrationResult(
            artifact.DocumentPath,
            artifact.GeneratedFiles,
            engineResult.Metadata,
            finishedAt);
    }

    private void LogStage(
        TranscriptionOrchestrationRequest request,
        string stage,
        string state,
        long elapsedMilliseconds)
    {
        if (!request.DiagnosticsEnabled)
        {
            return;
        }

        // native ASR障害の切り分けでは「どのEngineか」だけでなく、共通pipelineのどこまで進んだかが重要になる。
        // stageごとの経過時間を同じ構造で残し、UIやEngine固有実装へ診断責務を分散させない。
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
    bool DiagnosticsEnabled);

/// <summary>
/// Common pipelineの完了結果を表す
/// </summary>
public sealed record TranscriptionOrchestrationResult(
    string DocumentPath,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyDictionary<string, object?>? EngineMetadata,
    DateTimeOffset FinishedAt);
