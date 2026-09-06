using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper固有の認識処理だけをEngine契約へ接続する
/// </summary>
public sealed class WhisperTranscriptionEngine(
    WhisperSpeechRegionStrategy speechRegionStrategy,
    WhisperProcessorFactory processorFactory,
    WhisperRecognizer recognizer) : ITranscriptionEngine
{
    private static readonly TranscriptionAudioRequirements Requirements =
        new(16_000, 1, TranscriptionSampleFormat.Pcm16);

    /// <inheritdoc />
    public TranscriptionEngineId Id => WhisperEngineIdentity.EngineId;

    /// <inheritdoc />
    public TranscriptionAudioRequirements AudioRequirements => Requirements;

    /// <inheritdoc />
    public async Task<TranscriptionEngineResult> TranscribeAsync(
        TranscriptionEngineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options is not WhisperEngineOptions options)
        {
            throw new ArgumentException("Whisper Engineへ異なるoptions型が渡されました。", nameof(request));
        }

        var regions = await speechRegionStrategy.GetRegionsAsync(request.Audio, cancellationToken);
        if (regions.Count == 0)
        {
            return new TranscriptionEngineResult([]);
        }

        using var session = processorFactory.Create(options);
        var segments = await recognizer.RecognizeAsync(session, request.Audio, regions, cancellationToken);
        return new TranscriptionEngineResult(
            segments,
            new Dictionary<string, object?>
            {
                ["requestedBackend"] = options.ExecutionMode.ToString().ToLowerInvariant(),
                ["actualBackend"] = session.ActualRuntime
            });
    }
}
