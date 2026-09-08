using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech K2向けにSpeechRegionを最大25秒のASR呼び出し単位へ分割する
/// </summary>
/// <remarks>
/// VAD結果であるSpeechRegion自体は変更せず、K2固有の入力長制約をRecognitionChunkへ閉じ込める。
/// 25秒を超えるSpeechRegionだけRMS解析し、通常無音、forced split、短い末尾再配分の順に境界を決定する。
/// </remarks>
public sealed class ReazonSpeechRecognitionChunker : IDiagnosticRecognitionChunker
{
    private const long MaximumChunkSamples = 25L * ReazonSpeechRmsAnalyzer.RequiredSampleRate;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecognitionChunk>> CreateChunksAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default)
        => (await CreateChunksCoreAsync(audio, speechRegions, includeDiagnostics: false, cancellationToken)).Chunks;

    /// <inheritdoc />
    public Task<RecognitionChunkingDiagnosticResult> CreateChunksWithDiagnosticsAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default)
        => CreateChunksCoreAsync(audio, speechRegions, includeDiagnostics: true, cancellationToken);

    private static async Task<RecognitionChunkingDiagnosticResult> CreateChunksCoreAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speechRegions);
        cancellationToken.ThrowIfCancellationRequested();

        var chunks = new List<RecognitionChunk>();
        var traces = includeDiagnostics ? new List<RecognitionChunkDiagnosticTrace>() : null;
        foreach (var region in speechRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.EndSample <= region.StartSample)
            {
                throw new InvalidDataException($"SpeechRegion {region.SpeechRegionId} のsample範囲が不正です。");
            }

            if (region.Length <= MaximumChunkSamples)
            {
                AddChunk(chunks, traces, region.SpeechRegionId, region.StartSample, region.EndSample, "speech-region");
                continue;
            }

            // 同じSpeechRegionに対する通常無音探索、forced split、tail再配分で同一RMS系列を再利用する。
            // 分割ごとに音声を読み直すと長い発話ほどI/Oが増えるため、解析はSpeechRegion単位で一度だけ行う。
            var analysis = await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region, cancellationToken);
            AppendSplitChunks(chunks, traces, region, analysis, cancellationToken);
        }

        return new RecognitionChunkingDiagnosticResult(chunks, traces ?? []);
    }

    private static void AppendSplitChunks(
        ICollection<RecognitionChunk> chunks,
        ICollection<RecognitionChunkDiagnosticTrace>? traces,
        SpeechRegion region,
        ReazonSpeechRmsAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var chunkStart = region.StartSample;
        while (region.EndSample - chunkStart > MaximumChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var splitReason = string.Empty;
            var normalBoundary = ReazonSpeechSilenceBoundarySelector.Select(
                analysis,
                chunkStart,
                region.EndSample);
            var selectedSample = normalBoundary?.SelectedSample;
            if (selectedSample is not null)
            {
                splitReason = "silence";
            }

            if (selectedSample is null)
            {
                var forcedBoundary = ReazonSpeechForcedBoundarySelector.Select(
                    analysis,
                    chunkStart,
                    region.EndSample);
                selectedSample = forcedBoundary?.SelectedSample;
                if (selectedSample is not null)
                {
                    splitReason = "forced-rms";
                }
            }

            if (selectedSample is null)
            {
                // 25秒直前で切ると3秒未満のtailになるケースでは通常/forced候補を意図的に採用しない。
                // 直前chunkとtailを概ね等分する境界へ再配分し、短すぎるASR入力を残さない。
                var tailBoundary = ReazonSpeechTailBoundarySelector.Select(
                    analysis,
                    chunkStart,
                    region.EndSample);
                selectedSample = tailBoundary?.SelectedSample;
                if (selectedSample is not null)
                {
                    splitReason = "tail-redistribution";
                }
            }

            if (selectedSample is null
                || selectedSample.Value <= chunkStart
                || selectedSample.Value > chunkStart + MaximumChunkSamples
                || selectedSample.Value >= region.EndSample)
            {
                throw new InvalidDataException(
                    $"SpeechRegion {region.SpeechRegionId} を25秒以下へ安全に分割できませんでした。"
                    + $" StartSample={chunkStart}, EndSample={region.EndSample}");
            }

            AddChunk(chunks, traces, region.SpeechRegionId, chunkStart, selectedSample.Value, splitReason);
            chunkStart = selectedSample.Value;
        }

        if (region.EndSample > chunkStart)
        {
            AddChunk(chunks, traces, region.SpeechRegionId, chunkStart, region.EndSample, "region-end");
        }
    }

    private static void AddChunk(
        ICollection<RecognitionChunk> chunks,
        ICollection<RecognitionChunkDiagnosticTrace>? traces,
        int speechRegionId,
        long startSample,
        long endSample,
        string splitReason)
    {
        var chunk = CreateChunk(chunks.Count, speechRegionId, startSample, endSample);
        chunks.Add(chunk);
        traces?.Add(new RecognitionChunkDiagnosticTrace(
            chunk.RecognitionChunkId,
            chunk.SpeechRegionId,
            chunk.StartSample,
            chunk.EndSample,
            splitReason));
    }

    private static RecognitionChunk CreateChunk(
        int recognitionChunkId,
        int speechRegionId,
        long startSample,
        long endSample)
        => new(recognitionChunkId, speechRegionId, startSample, endSample);
}
