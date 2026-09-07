using Microsoft.Extensions.Logging;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech固有のrecognitionだけをEngine契約へ接続する
/// </summary>
public sealed class ReazonSpeechTranscriptionEngine(
    ISpeechRegionDetector speechRegionDetector,
    ReazonSpeechRecognizer recognizer,
    ILogger<ReazonSpeechTranscriptionEngine> logger) : ITranscriptionEngine
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

        const string provider = "cpu";
        const string decodingMethod = "greedy_search";
        if (request.Context.DiagnosticsEnabled)
        {
            logger.LogInformation(
                "ReazonSpeech recognition started. Provider={Provider}, Model={Model}, DecodingMethod={DecodingMethod}, RegionCount={RegionCount}",
                provider,
                options.ModelId,
                decodingMethod,
                regions.Count);
        }

        try
        {
            var segments = await recognizer.RecognizeAsync(
                request.Audio,
                regions,
                options,
                request.Context.DiagnosticsEnabled,
                cancellationToken);

            if (request.Context.DiagnosticsEnabled)
            {
                logger.LogInformation(
                    "ReazonSpeech recognition completed. Provider={Provider}, Model={Model}, DecodingMethod={DecodingMethod}, SegmentCount={SegmentCount}",
                    provider,
                    options.ModelId,
                    decodingMethod,
                    segments.Count);
            }

            return new TranscriptionEngineResult(
                segments,
                new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["decodingMethod"] = decodingMethod
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // native sherpa-onnxの失敗時にprovider/model/decode条件が欠落すると再現条件を追えないため、
            // 成功時metadataと同じ識別情報を構造化ログへ残す。
            logger.LogError(
                ex,
                "ReazonSpeech recognition failed. Provider={Provider}, Model={Model}, DecodingMethod={DecodingMethod}",
                provider,
                options.ModelId,
                decodingMethod);
            throw;
        }
    }
}
