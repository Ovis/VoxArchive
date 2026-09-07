using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech K2向けにSpeechRegionを最大25秒のASR呼び出し単位へ分割する
/// </summary>
/// <remarks>
/// VAD結果であるSpeechRegion自体は変更せず、K2固有の入力長制約をRecognitionChunkへ閉じ込める。
/// 25秒を超えるSpeechRegionだけRMS解析し、通常無音、forced split、短い末尾再配分の順に境界を決定する。
/// </remarks>
public sealed class ReazonSpeechRecognitionChunker : IRecognitionChunker
{
    private const long MaximumChunkSamples = 25L * ReazonSpeechRmsAnalyzer.RequiredSampleRate;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecognitionChunk>> CreateChunksAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(speechRegions);
        cancellationToken.ThrowIfCancellationRequested();

        var chunks = new List<RecognitionChunk>();
        foreach (var region in speechRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.EndSample <= region.StartSample)
            {
                throw new InvalidDataException($"SpeechRegion {region.SpeechRegionId} のsample範囲が不正です。");
            }

            if (region.Length <= MaximumChunkSamples)
            {
                chunks.Add(CreateChunk(chunks.Count, region.SpeechRegionId, region.StartSample, region.EndSample));
                continue;
            }

            // 同じSpeechRegionに対する通常無音探索、forced split、tail再配分で同一RMS系列を再利用する。
            // 分割ごとに音声を読み直すと長い発話ほどI/Oが増えるため、解析はSpeechRegion単位で一度だけ行う。
            var analysis = await ReazonSpeechRmsAnalyzer.AnalyzeAsync(audio, region, cancellationToken);
            AppendSplitChunks(chunks, region, analysis, cancellationToken);
        }

        return chunks;
    }

    private static void AppendSplitChunks(
        ICollection<RecognitionChunk> chunks,
        SpeechRegion region,
        ReazonSpeechRmsAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var chunkStart = region.StartSample;
        while (region.EndSample - chunkStart > MaximumChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalBoundary = ReazonSpeechSilenceBoundarySelector.Select(
                analysis,
                chunkStart,
                region.EndSample);
            var selectedSample = normalBoundary?.SelectedSample;

            if (selectedSample is null)
            {
                var forcedBoundary = ReazonSpeechForcedBoundarySelector.Select(
                    analysis,
                    chunkStart,
                    region.EndSample);
                selectedSample = forcedBoundary?.SelectedSample;
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

            chunks.Add(CreateChunk(chunks.Count, region.SpeechRegionId, chunkStart, selectedSample.Value));
            chunkStart = selectedSample.Value;
        }

        if (region.EndSample > chunkStart)
        {
            chunks.Add(CreateChunk(chunks.Count, region.SpeechRegionId, chunkStart, region.EndSample));
        }
    }

    private static RecognitionChunk CreateChunk(
        int recognitionChunkId,
        int speechRegionId,
        long startSample,
        long endSample)
        => new(recognitionChunkId, speechRegionId, startSample, endSample);
}
