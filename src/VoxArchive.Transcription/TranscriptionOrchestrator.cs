using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine非依存の文字起こしpipelineを順序どおり実行する
/// </summary>
public sealed class TranscriptionOrchestrator(
    TranscriptionEngineRegistry engineRegistry,
    TranscriptionAudioPreparationService audioPreparationService,
    TranscriptionEngineResultValidator resultValidator,
    TranscriptionSpeakerLabelService speakerLabelService,
    TranscriptionArtifactService artifactService)
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

        await using var preparedAudio = await audioPreparationService.PrepareAsync(
            request.SourceRecordingPath,
            request.SpeakerGainDb,
            request.MicrophoneGainDb,
            engine.AudioRequirements,
            cancellationToken);

        // Prepared Audioの所有権はCommon pipelineにある。EngineはborrowするだけでDisposeしないため、
        // recognition後のvalidator/post-processが終わるまで同じ音声を安全に再利用できる。
        var engineResult = await engine.TranscribeAsync(
            new TranscriptionEngineRequest(preparedAudio, request.EngineOptions),
            cancellationToken);

        resultValidator.Validate(engineResult, preparedAudio.Duration);
        var labeled = speakerLabelService.Apply(
            request.SourceRecordingPath,
            engineResult.Segments,
            cancellationToken);
        var finishedAt = DateTimeOffset.Now;
        var artifact = await artifactService.WriteAsync(
            request.SourceRecordingPath,
            request.EngineId,
            request.ArtifactOptions,
            engineResult,
            labeled,
            finishedAt,
            cancellationToken);

        return new TranscriptionOrchestrationResult(
            artifact.DocumentPath,
            artifact.GeneratedFiles,
            engineResult.Metadata,
            finishedAt);
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
    TranscriptionArtifactOptions ArtifactOptions);

/// <summary>
/// Common pipelineの完了結果を表す
/// </summary>
public sealed record TranscriptionOrchestrationResult(
    string DocumentPath,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyDictionary<string, object?>? EngineMetadata,
    DateTimeOffset FinishedAt);
