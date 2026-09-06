using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engine結果とCommon post-process結果からcanonical documentと派生artifactを確定する
/// </summary>
public sealed class TranscriptionArtifactService(
    TranscriptionDocumentStore documentStore,
    TranscriptionExportService exportService)
{
    /// <summary>
    /// canonical JSONを必ず保存し、指定されたTXT/SRT/VTTを派生生成する
    /// </summary>
    public async Task<TranscriptionArtifactResult> WriteAsync(
        string sourceRecordingPath,
        TranscriptionEngineId engineId,
        TranscriptionArtifactOptions options,
        TranscriptionEngineResult engineResult,
        IReadOnlyList<LabeledTranscriptionSegment> labeledSegments,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordingPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engineResult);
        ArgumentNullException.ThrowIfNull(labeledSegments);

        var document = new TranscriptionDocument
        {
            SourceFileName = Path.GetFileName(sourceRecordingPath),
            EngineId = engineId.Value,
            ModelId = options.ModelId?.Value,
            CreatedAt = createdAt,
            EngineMetadata = engineResult.Metadata,
            Segments = labeledSegments.Select(x => new TranscriptionDocumentSegment(
                x.Segment.Start.TotalSeconds,
                x.Segment.End.TotalSeconds,
                x.Segment.Text,
                x.SpeakerLabel)).ToArray()
        };

        var documentPath = BuildDocumentPath(sourceRecordingPath, engineId, options.ModelId);
        await documentStore.SaveAsync(documentPath, document, cancellationToken);
        var derived = await exportService.WriteDerivedAsync(documentPath, document, options.Formats, cancellationToken);
        return new TranscriptionArtifactResult(documentPath, [documentPath, .. derived]);
    }

    /// <summary>
    /// Engine/Model IDから衝突しないcanonical document pathを生成する
    /// </summary>
    public static string BuildDocumentPath(
        string sourceRecordingPath,
        TranscriptionEngineId engineId,
        TranscriptionModelId? modelId)
    {
        var directory = Path.GetDirectoryName(sourceRecordingPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourceRecordingPath);
        var suffix = modelId is null
            ? engineId.Value
            : $"{engineId.Value}-{modelId.Value.Value}";
        return Path.Combine(directory, $"{fileName}-{suffix}.json");
    }
}

/// <summary>
/// artifact生成結果を保持する
/// </summary>
public sealed record TranscriptionArtifactResult(
    string DocumentPath,
    IReadOnlyList<string> GeneratedFiles);
