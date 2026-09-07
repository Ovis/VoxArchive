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
            ExecutionSnapshot = options.ExecutionSnapshot,
            CreatedAt = createdAt,
            EngineMetadata = engineResult.Metadata,
            Segments = labeledSegments.Select(x => new TranscriptionDocumentSegment(
                x.Segment.Start.TotalSeconds,
                x.Segment.End.TotalSeconds,
                x.Segment.Text,
                x.SpeakerLabel)).ToArray()
        };

        var documentPath = BuildDocumentPath(sourceRecordingPath, engineId, options.ModelId, options.FileNameSuffix);
        await documentStore.SaveAsync(documentPath, document, cancellationToken);
        var derived = await exportService.WriteDerivedAsync(documentPath, document, options.Formats, cancellationToken);
        return new TranscriptionArtifactResult(documentPath, [documentPath, .. derived]);
    }

    /// <summary>
    /// Engineが指定したsuffix、またはEngine/Model IDからcanonical document pathを生成する
    /// </summary>
    public static string BuildDocumentPath(
        string sourceRecordingPath,
        TranscriptionEngineId engineId,
        TranscriptionModelId? modelId,
        string? fileNameSuffix = null)
    {
        var directory = Path.GetDirectoryName(sourceRecordingPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourceRecordingPath);
        var suffix = string.IsNullOrWhiteSpace(fileNameSuffix)
            ? modelId is null
                ? engineId.Value
                : $"{engineId.Value}-{modelId.Value.Value}"
            : fileNameSuffix.Trim();

        ValidateFileNameSuffix(suffix);
        return Path.Combine(directory, $"{fileName}-{suffix}.json");
    }

    private static void ValidateFileNameSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            throw new ArgumentException("Artifact file name suffix must not be empty.", nameof(suffix));
        }

        // Engine capabilityの値をPath.Combineへ直接渡すと録音ディレクトリ外へ書き込めるため、
        // suffixは単一ファイル名要素に限定する。
        if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || suffix.Contains(Path.DirectorySeparatorChar)
            || suffix.Contains(Path.AltDirectorySeparatorChar)
            || suffix is "." or "..")
        {
            throw new ArgumentException($"Invalid artifact file name suffix: {suffix}", nameof(suffix));
        }
    }
}

/// <summary>
/// artifact生成結果を保持する
/// </summary>
public sealed record TranscriptionArtifactResult(
    string DocumentPath,
    IReadOnlyList<string> GeneratedFiles);
