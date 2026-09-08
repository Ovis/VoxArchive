using System.Text.Json;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeech K2向けにSpeechRegionを最大25秒のASR呼び出し単位へ分割する
/// </summary>
/// <remarks>
/// VAD結果であるSpeechRegion自体は変更せず、K2固有の入力長制約をRecognitionChunkへ閉じ込める。
/// 25秒を超えるSpeechRegionだけRMS解析し、通常無音、forced split、短い末尾再配分の順に境界を決定する。
/// Engine固有の処理として具象型を直接利用し、共通Chunker Interfaceは設けない。
/// </remarks>
public sealed class ReazonSpeechRecognitionChunker
{
    private const long MaximumChunkSamples = 25L * ReazonSpeechRmsAnalyzer.RequiredSampleRate;

    /// <summary>指定した発話区間からReazonSpeech向けRecognitionChunkを生成する</summary>
    public async Task<IReadOnlyList<RecognitionChunk>> CreateChunksAsync(
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken = default)
        => (await CreateChunksCoreAsync(audio, speechRegions, includeDiagnostics: false, cancellationToken)).Chunks;

    /// <summary>RecognitionChunkと分割理由を同時に生成する</summary>
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
                AddChunk(
                    chunks,
                    traces,
                    region.SpeechRegionId,
                    region.StartSample,
                    region.EndSample,
                    "speech-region",
                    splitDetails: null);
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
            JsonElement? splitDetails = null;
            var normalBoundary = ReazonSpeechSilenceBoundarySelector.Select(
                analysis,
                chunkStart,
                region.EndSample);
            var selectedSample = normalBoundary?.SelectedSample;
            if (normalBoundary is not null)
            {
                splitReason = "silence";
                if (traces is not null)
                {
                    splitDetails = JsonSerializer.SerializeToElement(new
                    {
                        p20Rms = analysis.P20Rms,
                        targetSample = normalBoundary.TargetSample,
                        searchStartSample = normalBoundary.SearchStartSample,
                        searchEndSample = normalBoundary.SearchEndSample,
                        selectedSample = normalBoundary.SelectedSample,
                        silenceStartSample = normalBoundary.SilenceStartSample,
                        silenceEndSample = normalBoundary.SilenceEndSample
                    });
                }
            }

            if (selectedSample is null)
            {
                var forcedBoundary = ReazonSpeechForcedBoundarySelector.Select(
                    analysis,
                    chunkStart,
                    region.EndSample);
                selectedSample = forcedBoundary?.SelectedSample;
                if (forcedBoundary is not null)
                {
                    splitReason = "forced-rms";
                    if (traces is not null)
                    {
                        splitDetails = JsonSerializer.SerializeToElement(new
                        {
                            p20Rms = analysis.P20Rms,
                            targetSample = forcedBoundary.TargetSample,
                            searchStartSample = forcedBoundary.SearchStartSample,
                            searchEndSample = forcedBoundary.SearchEndSample,
                            selectedSample = forcedBoundary.SelectedSample,
                            frameStartSample = forcedBoundary.FrameStartSample,
                            frameEndSample = forcedBoundary.FrameEndSample,
                            rms = forcedBoundary.Rms
                        });
                    }
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
                if (tailBoundary is not null)
                {
                    splitReason = "tail-redistribution";
                    if (traces is not null)
                    {
                        splitDetails = JsonSerializer.SerializeToElement(new
                        {
                            p20Rms = analysis.P20Rms,
                            targetSample = tailBoundary.TargetSample,
                            searchStartSample = tailBoundary.SearchStartSample,
                            searchEndSample = tailBoundary.SearchEndSample,
                            selectedSample = tailBoundary.SelectedSample,
                            usedSilence = tailBoundary.UsedSilence,
                            silenceStartSample = tailBoundary.SilenceStartSample,
                            silenceEndSample = tailBoundary.SilenceEndSample,
                            rms = tailBoundary.Rms
                        });
                    }
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

            AddChunk(
                chunks,
                traces,
                region.SpeechRegionId,
                chunkStart,
                selectedSample.Value,
                splitReason,
                splitDetails);
            chunkStart = selectedSample.Value;
        }

        if (region.EndSample > chunkStart)
        {
            AddChunk(
                chunks,
                traces,
                region.SpeechRegionId,
                chunkStart,
                region.EndSample,
                "region-end",
                splitDetails: null);
        }
    }

    private static void AddChunk(
        ICollection<RecognitionChunk> chunks,
        ICollection<RecognitionChunkDiagnosticTrace>? traces,
        int speechRegionId,
        long startSample,
        long endSample,
        string splitReason,
        JsonElement? splitDetails)
    {
        var chunk = CreateChunk(chunks.Count, speechRegionId, startSample, endSample);
        chunks.Add(chunk);
        traces?.Add(new RecognitionChunkDiagnosticTrace(
            chunk.RecognitionChunkId,
            chunk.SpeechRegionId,
            chunk.StartSample,
            chunk.EndSample,
            splitReason,
            splitDetails));
    }

    private static RecognitionChunk CreateChunk(
        int recognitionChunkId,
        int speechRegionId,
        long startSample,
        long endSample)
        => new(recognitionChunkId, speechRegionId, startSample, endSample);
}
