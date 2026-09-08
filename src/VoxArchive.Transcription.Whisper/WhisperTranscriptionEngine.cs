using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper固有の認識処理だけをEngine契約へ接続する
/// </summary>
public sealed class WhisperTranscriptionEngine(
    WhisperRecognitionChunker recognitionChunker,
    WhisperProcessorFactory processorFactory,
    WhisperRecognizer recognizer,
    ILogger<WhisperTranscriptionEngine> logger) : ITranscriptionEngine
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

        RecognitionChunkingDiagnosticResult? chunkingDiagnostic = null;
        IReadOnlyList<RecognitionChunk> chunks;
        var chunkingStopwatch = request.Context.DiagnosticsEnabled ? Stopwatch.StartNew() : null;
        if (request.Context.DiagnosticsEnabled)
        {
            chunkingDiagnostic = await recognitionChunker.CreateChunksWithDiagnosticsAsync(
                request.Audio,
                request.SpeechRegions,
                cancellationToken);
            chunks = chunkingDiagnostic.Chunks;
        }
        else
        {
            chunks = await recognitionChunker.CreateChunksAsync(request.Audio, request.SpeechRegions, cancellationToken);
        }
        chunkingStopwatch?.Stop();

        if (chunks.Count == 0)
        {
            return new TranscriptionEngineResult(
                [],
                Diagnostics: request.Context.DiagnosticsEnabled
                    ? new TranscriptionEngineDiagnosticTrace(
                        chunkingDiagnostic?.Traces ?? [],
                        chunkingStopwatch?.ElapsedMilliseconds ?? 0,
                        0,
                        [])
                    : null);
        }

        using var session = processorFactory.Create(options);
        var requestedBackend = options.ExecutionMode.ToString().ToLowerInvariant();
        var actualBackend = session.ActualRuntime;

        // Whisper.netはAuto指定時に複数runtime候補から実際に利用するbackendを選ぶため、
        // 設定値だけではなくロード済みruntimeも必ず記録して実機診断で確認できるようにする。
        logger.LogInformation(
            "Whisper backend selected. RequestedBackend={RequestedBackend}, ActualBackend={ActualBackend}",
            requestedBackend,
            actualBackend);

        try
        {
            var asrStopwatch = request.Context.DiagnosticsEnabled ? Stopwatch.StartNew() : null;
            var recognition = await recognizer.RecognizeAsync(
                session,
                request.Audio,
                chunks,
                request.Context.DiagnosticsEnabled,
                cancellationToken);
            asrStopwatch?.Stop();

            return new TranscriptionEngineResult(
                recognition.Segments,
                new Dictionary<string, object?>
                {
                    ["requestedBackend"] = requestedBackend,
                    ["actualBackend"] = actualBackend
                },
                request.Context.DiagnosticsEnabled
                    ? new TranscriptionEngineDiagnosticTrace(
                        chunkingDiagnostic?.Traces ?? [],
                        chunkingStopwatch?.ElapsedMilliseconds ?? 0,
                        asrStopwatch?.ElapsedMilliseconds ?? 0,
                        recognition.Diagnostics)
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // native runtime固有の障害を後から切り分けられるよう、認識失敗時にも同じbackend情報を残す。
            logger.LogError(
                ex,
                "Whisper transcription failed. RequestedBackend={RequestedBackend}, ActualBackend={ActualBackend}",
                requestedBackend,
                actualBackend);
            throw;
        }
    }
}
