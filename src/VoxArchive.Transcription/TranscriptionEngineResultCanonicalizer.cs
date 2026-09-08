using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// Engineが返した認識結果をCommonのcanonical規則へ正規化する
/// </summary>
/// <remarks>
/// EngineごとにTrim、空白除外、時刻丸めを行うと同じ入力でも結果規則が分岐するため、
/// canonical artifactへ進む前にCommonで一度だけ適用する。文字列はTrim以外の正規化を行わない。
/// </remarks>
public sealed class TranscriptionEngineResultCanonicalizer
{
    /// <summary>
    /// Engine結果をPrepared Audioのsample timelineへ正規化する
    /// </summary>
    /// <param name="result">Engineが返した未canonical結果</param>
    /// <param name="audio">sample rateと実sample数の正本となるPrepared Audio</param>
    public TranscriptionEngineResult Canonicalize(
        TranscriptionEngineResult result,
        IPreparedTranscriptionAudio audio)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.Format.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audio), "Prepared AudioのSampleRateは正数である必要があります。");
        }

        var normalized = new List<(RecognizedTranscriptionSegment Segment, long StartSample, int OriginalIndex)>();
        for (var i = 0; i < result.Segments.Count; i++)
        {
            var segment = result.Segments[i]
                ?? throw new InvalidDataException($"Engine result segment[{i}]がnullです。");
            if (segment.End < segment.Start)
            {
                throw new InvalidDataException($"Engine result segment[{i}]のEndがStartより前です。");
            }

            // raw文字列は詳細診断側で保持し、canonical結果にはTrimだけを適用する。
            // Unicode正規化や句読点補正はASR結果の比較可能性を損なうため行わない。
            var text = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var startSample = SecondsToStartSample(segment.Start.TotalSeconds, audio.Format.SampleRate, audio.SampleCount);
            var endSample = SecondsToEndSample(segment.End.TotalSeconds, audio.Format.SampleRate, audio.SampleCount);
            if (endSample < startSample)
            {
                endSample = startSample;
            }

            normalized.Add((
                segment with
                {
                    Start = SamplesToTimeSpan(startSample, audio.Format.SampleRate),
                    End = SamplesToTimeSpan(endSample, audio.Format.SampleRate),
                    Text = text
                },
                startSample,
                i));
        }

        // 同一StartSampleではEngineが返した順序を維持する。Overlap自体は動かさず、
        // downstreamが実際のASR timelineをそのまま観測できるようにする。
        var segments = normalized
            .OrderBy(x => x.StartSample)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Segment)
            .ToArray();

        return result with { Segments = segments };
    }

    private static long SecondsToStartSample(double seconds, int sampleRate, long sampleCount)
    {
        var value = double.IsFinite(seconds) ? Math.Floor(seconds * sampleRate) : 0d;
        return ClampSample(value, sampleCount);
    }

    private static long SecondsToEndSample(double seconds, int sampleRate, long sampleCount)
    {
        var value = double.IsFinite(seconds) ? Math.Ceiling(seconds * sampleRate) : 0d;
        return ClampSample(value, sampleCount);
    }

    private static long ClampSample(double sample, long sampleCount)
    {
        if (sample <= 0d)
        {
            return 0L;
        }
        if (sample >= sampleCount)
        {
            return sampleCount;
        }
        return checked((long)sample);
    }

    private static TimeSpan SamplesToTimeSpan(long samples, int sampleRate)
        => TimeSpan.FromSeconds(samples / (double)sampleRate);
}
