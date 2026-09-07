using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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

                    var (start, end) = NormalizeSegmentTimeline(
                        result.Start,
                        result.End,
                        region,
                        audio.Format.SampleRate,
                        audio.Duration);
                    collected.Add(new RecognizedTranscriptionSegment(start, end, text));
                }
            }
            finally
            {
                TryDelete(segmentWavePath);
            }
        }

        return MergeAdjacentSegments(collected);
    }

    /// <summary>
    /// Whisper.netが返すVAD区間内の相対timestampをCommon契約のabsolute timelineへ正規化する
    /// </summary>
    /// <remarks>
    /// Whisperは音声末尾で量子化誤差等により、実際に渡した区間より少し後ろのEndを返すことがある。
    /// Common validatorを緩めると他Engineの契約違反まで隠すため、Whisper固有adapterで実際の入力区間へ収める。
    /// SpeechRegion自体はsample座標を正本とし、Engine境界でのみTimeSpanへ変換する。
    /// </remarks>
    internal static (TimeSpan Start, TimeSpan End) NormalizeSegmentTimeline(
        TimeSpan relativeStart,
        TimeSpan relativeEnd,
        SpeechRegion region,
        int sampleRate,
        TimeSpan audioDuration)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var regionStart = SamplesToTimeSpan(region.StartSample, sampleRate);
        var regionEnd = SamplesToTimeSpan(region.EndSample, sampleRate);
        if (regionEnd > audioDuration)
        {
            regionEnd = audioDuration;
        }
        if (regionEnd < regionStart)
        {
            regionEnd = regionStart;
        }

        var absoluteStart = regionStart + relativeStart;
        var absoluteEnd = regionStart + relativeEnd;
        var start = Clamp(absoluteStart, regionStart, regionEnd);
        var end = Clamp(absoluteEnd, start, regionEnd);
        return (start, end);
    }

    private static async Task WriteRegionAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegion region,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(source);
        var sampleRate = reader.WaveFormat.SampleRate;
        var provider = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOver = SamplesToTimeSpan(region.StartSample, sampleRate),
            Take = SamplesToTimeSpan(region.Length, sampleRate)
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

    private static TimeSpan SamplesToTimeSpan(long samples, int sampleRate)
        => TimeSpan.FromSeconds(samples / (double)sampleRate);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }
        return value > maximum ? maximum : value;
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
