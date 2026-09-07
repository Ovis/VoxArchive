using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech固有のrecognitionだけをEngine契約へ接続する
/// </summary>
public sealed class ReazonSpeechTranscriptionEngine(
    ISpeechRegionDetector speechRegionDetector,
    ReazonSpeechRecognizer recognizer) : ITranscriptionEngine
{
    private static readonly TranscriptionAudioRequirements Requirements =
        new(16_000, 1, TranscriptionSampleFormat.Pcm16);

    /// <inheritdoc />
    public TranscriptionEngineId Id => ReazonSpeechEngineIdentity.EngineId;

    /// <inheritdoc />
    public TranscriptionAudioRequirements AudioRequirements => Requirements;

    /// <inheritdoc />
    public async Task<TranscriptionEngineResult> TranscribeAsync(
        TranscriptionEngineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options is not ReazonSpeechEngineOptions options)
        {
            throw new ArgumentException("ReazonSpeech Engineへ異なるoptions型が渡されました。", nameof(request));
        }

        var regions = await speechRegionDetector.DetectAsync(request.Audio, cancellationToken);
        if (regions.Count == 0)
        {
            return new TranscriptionEngineResult([]);
        }

        var segments = await recognizer.RecognizeAsync(
            request.Audio,
            regions,
            options,
            request.Context.DiagnosticsEnabled,
            cancellationToken);
        return new TranscriptionEngineResult(
            segments,
            new Dictionary<string, object?>
            {
                ["provider"] = "cpu",
                ["decodingMethod"] = "greedy_search"
            });
    }
}
