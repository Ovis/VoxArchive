using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// K2が返したsubword timestampを診断可能なsample座標へ変換する
/// </summary>
/// <remarks>
/// ReazonSpeech公式K2 APIはsubwordごとの単一点timestampだけを返すため、存在しない終了時刻は推測しない。
/// raw秒、0.9秒補正後のchunk-relative sample、Prepared Audio上のabsolute sampleを対応付けて保持する。
/// </remarks>
internal static class ReazonSpeechK2TokenTimestampTraceBuilder
{
    /// <summary>
    /// sherpa-onnxのtoken/timestamp配列からtraceを生成する
    /// </summary>
    /// <param name="tokens">K2が返したsubword token</param>
    /// <param name="timestamps">K2が返したpadding込み入力上の秒timestamp</param>
    /// <param name="chunk">認識対象RecognitionChunk</param>
    internal static IReadOnlyList<ReazonSpeechK2TokenTimestampTrace> Build(
        IReadOnlyList<string>? tokens,
        IReadOnlyList<float>? timestamps,
        RecognitionChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (tokens is null || timestamps is null || tokens.Count == 0 || timestamps.Count == 0)
        {
            return [];
        }

        // sherpa-onnxの契約ではtoken数とtimestamp数は一致するが、native境界の異常で不一致になっても
        // 認識本文まで失わないよう、対応可能な要素だけをtrace化する。
        var count = Math.Min(tokens.Count, timestamps.Count);
        var result = new List<ReazonSpeechK2TokenTimestampTrace>(count);
        for (var i = 0; i < count; i++)
        {
            var rawSeconds = timestamps[i];
            if (!float.IsFinite(rawSeconds))
            {
                continue;
            }

            var correctedSample = ReazonSpeechK2TimestampNormalizer.NormalizePoint(rawSeconds, chunk.Length);
            result.Add(new ReazonSpeechK2TokenTimestampTrace(
                i,
                tokens[i],
                rawSeconds,
                correctedSample,
                checked(chunk.StartSample + correctedSample)));
        }
        return result;
    }
}

/// <summary>
/// K2 subword timestampのraw値と補正後timelineを対応付ける
/// </summary>
internal sealed record ReazonSpeechK2TokenTimestampTrace(
    int TokenIndex,
    string Token,
    double RawSeconds,
    long CorrectedChunkSample,
    long AbsoluteSample);
