using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// WhisperProcessorへRecognitionChunkを渡し、absolute timelineのEngine segmentへ変換する
/// </summary>
public sealed class WhisperRecognizer
{
    private const double VadMergeGapMilliseconds = 300d;

    /// <summary>
    /// 指定したRecognitionChunkを順次認識する
    /// </summary>
    public async Task<WhisperRecognitionResult> RecognizeAsync(
        WhisperProcessorSession session,
        IPreparedTranscriptionAudio audio,
        IReadOnlyList<RecognitionChunk> chunks,
        bool diagnosticsEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(chunks);

        var collected = new List<RecognizedTranscriptionSegment>();
        var diagnosticResults = diagnosticsEnabled
            ? new List<AsrResultDiagnosticTrace>()
            : null;

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-seg-{Guid.NewGuid():N}.wav");
            try
            {
                await WriteChunkAsync(audio, chunk, segmentWavePath, cancellationToken);
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
                    var rawText = result.Text ?? string.Empty;
                    var (start, end) = NormalizeSegmentTimeline(
                        result.Start,
                        result.End,
                        chunk,
                        audio.Format.SampleRate,
                        audio.Duration);

                    if (diagnosticsEnabled)
                    {
                        var timestampTrace = JsonSerializer.SerializeToElement(new
                        {
                            rawStartSeconds = result.Start.TotalSeconds,
                            rawEndSeconds = result.End.TotalSeconds,
                            correctedStartSeconds = result.Start.TotalSeconds,
                            correctedEndSeconds = result.End.TotalSeconds,
                            clampedAbsoluteStartSeconds = start.TotalSeconds,
                            clampedAbsoluteEndSeconds = end.TotalSeconds,
                            absoluteStartSample = ToCanonicalStartSample(start, audio.Format.SampleRate, audio.SampleCount),
                            absoluteEndSample = ToCanonicalEndSample(end, audio.Format.SampleRate, audio.SampleCount)
                        });
                        var discarded = string.IsNullOrWhiteSpace(rawText);
                        diagnosticResults!.Add(new AsrResultDiagnosticTrace(
                            chunk.RecognitionChunkId,
                            rawText,
                            timestampTrace,
                            discarded,
                            discarded ? "whitespace-only" : null));
                    }

                    var text = rawText.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    // 既存の同一chunk内merge挙動は維持するため、mergeへ渡す文字列は従来どおりTrim済みとする。
                    // raw文字列そのものは上のdiagnostic traceに保持しているため情報は失われない。
                    collected.Add(new RecognizedTranscriptionSegment(
                        start,
                        end,
                        text,
                        chunk.RecognitionChunkId));
                }
            }
            finally
            {
                TryDelete(segmentWavePath);
            }
        }

        return new WhisperRecognitionResult(
            MergeAdjacentSegments(collected),
            diagnosticResults ?? []);
    }

    /// <summary>
    /// Whisper.netが返すRecognitionChunk内の相対timestampをCommon契約のabsolute timelineへ正規化する
    /// </summary>
    /// <remarks>
    /// Whisperは音声末尾で量子化誤差等により、実際に渡した区間より少し後ろのEndを返すことがある。
    /// Common validatorを緩めると他Engineの契約違反まで隠すため、Whisper固有adapterで実際の入力区間へ収める。
    /// RecognitionChunk自体はsample座標を正本とし、Engine境界でのみTimeSpanへ変換する。
    /// </remarks>
    internal static (TimeSpan Start, TimeSpan End) NormalizeSegmentTimeline(
        TimeSpan relativeStart,
        TimeSpan relativeEnd,
        RecognitionChunk chunk,
        int sampleRate,
        TimeSpan audioDuration)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var chunkStart = SamplesToTimeSpan(chunk.StartSample, sampleRate);
        var chunkEnd = SamplesToTimeSpan(chunk.EndSample, sampleRate);
        if (chunkEnd > audioDuration)
        {
            chunkEnd = audioDuration;
        }
        if (chunkEnd < chunkStart)
        {
            chunkEnd = chunkStart;
        }

        var absoluteStart = chunkStart + relativeStart;
        var absoluteEnd = chunkStart + relativeEnd;
        var start = Clamp(absoluteStart, chunkStart, chunkEnd);
        var end = Clamp(absoluteEnd, start, chunkEnd);
        return (start, end);
    }

    private static async Task WriteChunkAsync(
        IPreparedTranscriptionAudio audio,
        RecognitionChunk chunk,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = await audio.OpenReadAsync(cancellationToken);
        using var reader = new WaveFileReader(source);
        var sampleRate = reader.WaveFormat.SampleRate;
        var provider = new OffsetSampleProvider(reader.ToSampleProvider())
        {
            SkipOver = SamplesToTimeSpan(chunk.StartSample, sampleRate),
            Take = SamplesToTimeSpan(chunk.Length, sampleRate)
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

        var ordered = segments
            .Select((segment, index) => (segment, index))
            .OrderBy(x => x.segment.Start)
            .ThenBy(x => x.index)
            .Select(x => x.segment)
            .ToList();
        var merged = new List<RecognizedTranscriptionSegment>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];

            // RecognitionChunkをまたいでsegmentを結合するとASR結果から生成元chunkを追跡できなくなるため、
            // 従来の近接segment結合は同一chunk内に限定する。
            if (current.RecognitionChunkId == last.RecognitionChunkId
                && (current.Start - last.End).TotalMilliseconds <= VadMergeGapMilliseconds)
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

    private static long ToCanonicalStartSample(TimeSpan value, int sampleRate, long sampleCount)
        => Math.Clamp((long)Math.Floor(value.TotalSeconds * sampleRate), 0L, sampleCount);

    private static long ToCanonicalEndSample(TimeSpan value, int sampleRate, long sampleCount)
        => Math.Clamp((long)Math.Ceiling(value.TotalSeconds * sampleRate), 0L, sampleCount);

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

/// <summary>
/// Whisper Recognizerが返す未canonical segmentとnative raw診断traceを保持する
/// </summary>
public sealed record WhisperRecognitionResult(
    IReadOnlyList<RecognizedTranscriptionSegment> Segments,
    IReadOnlyList<AsrResultDiagnosticTrace> Diagnostics);
