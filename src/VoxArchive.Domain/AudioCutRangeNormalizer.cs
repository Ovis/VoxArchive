namespace VoxArchive.Domain;

/// <summary>
/// 音声編集で使用するカット区間を、開始時刻順かつ非重複・非隣接の状態へ正規化する
/// </summary>
public static class AudioCutRangeNormalizer
{
    /// <summary>
    /// 指定されたカット区間を開始時刻順に並べ、重複または隣接する区間を結合する
    /// </summary>
    /// <param name="ranges">元音声時間軸上のカット区間</param>
    /// <returns>開始時刻順かつ非重複・非隣接のカット区間</returns>
    public static IReadOnlyList<AudioCutRange> Normalize(IEnumerable<AudioCutRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var ordered = ranges.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
        if (ordered.Length == 0)
        {
            return Array.Empty<AudioCutRange>();
        }

        var normalized = new List<AudioCutRange>(ordered.Length);
        var current = ordered[0];

        for (var i = 1; i < ordered.Length; i++)
        {
            var next = ordered[i];

            // 隣接区間を別々に残すと、実際には存在しない接続点へCrossfadeを適用することになるため、
            // 重複区間と同様に一つの連続した削除区間として扱う。
            if (next.Start <= current.End)
            {
                if (next.End > current.End)
                {
                    current = new AudioCutRange(current.Start, next.End);
                }

                continue;
            }

            normalized.Add(current);
            current = next;
        }

        normalized.Add(current);
        return normalized;
    }
}
