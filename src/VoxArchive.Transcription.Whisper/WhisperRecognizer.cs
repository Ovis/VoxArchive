using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// WhisperProcessorへVAD区間を渡し、absolute timelineのEngine segmentへ変換する
/// </summary>
public sealed class WhisperRecognizer
{
    private const double VadMergeGapMilliseconds = 300d;

    /// <summary>
    /// 指定区間を順次認識する
    /// </summary>
    public async Task<IReadOnlyList<RecognizedTranscriptionSegment>> RecognizeAsync(
        WhisperProcessorSession session,
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(regions);

        var collected = new List<RecognizedTranscriptionSegment>();
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-seg-{Guid.NewGuid():N}.wav");
            try
            {
                await WriteRegionAsync(audio, region, segmentWavePath, cancellationToken);
                await using var segmentStream = new FileStream(
                    segmentWavePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    useAsync: true);

                // Whisper.net 1.9.1はProcessAsync(Stream, CancellationToken)を公開しているため、
                // 旧実装のreflectionは不要である。native処理中のCancellationToken処理もSDKへそのまま委譲する。
                await foreach (var result in session.Processor.ProcessAsync(segmentStream, cancellationToken))
                {
                    var text = result.Text?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    collected.Add(new RecognizedTranscriptionSegment(
                        result.Start + region.Start,
                        result.End + region.Start,
                        text));
                }
            }
            finally
            {
                TryDelete(segmentWavePath);
            }
        }

        return MergeAdjacentSegments(collected);
    }

    private static async Task WriteRegionAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegion region,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(source);
        var provider = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOver = region.Start,
            Take = region.Duration
        };

        await Task.Run(() => WaveFileWriter.CreateWaveFile16(destinationPath, provider), cancellationToken);
    }

    private static IReadOnlyList<RecognizedTranscriptionSegment> MergeAdjacentSegments(
        IReadOnlyList<RecognizedTranscriptionSegment> segments)
    {
        if (segments.Count <= 1)
        {
            return segments;
        }

        var ordered = segments.OrderBy(x => x.Start).ToList();
        var merged = new List<RecognizedTranscriptionSegment>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];
            if ((current.Start - last.End).TotalMilliseconds <= VadMergeGapMilliseconds)
            {
                merged[^1] = last with
                {
                    End = current.End > last.End ? current.End : last.End,
                    Text = MergeText(last.Text, current.Text)
                };
            }
            else
            {
                merged.Add(current);
            }
        }
        return merged;
    }

    private static string MergeText(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }
        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }
        return $"{left} {right}";
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Engine内adapterが生成したGUID名の一時ファイルなので、削除失敗を認識失敗へ波及させない。
        }
    }
}
