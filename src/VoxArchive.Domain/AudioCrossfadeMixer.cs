namespace VoxArchive.Domain;

/// <summary>
/// Cutで離れた2区間の接続点を線形Crossfadeする
/// </summary>
/// <remarks>
/// 呼び出し側は同じチャンネル・同じフレーム数の末尾側と先頭側を渡す。
/// PCM全体を保持せず、接続点周辺の最大5ms分だけをバッファすれば利用できる。
/// </remarks>
public static class AudioCrossfadeMixer
{
    /// <summary>
    /// 左区間の末尾と右区間の先頭を線形Crossfadeする
    /// </summary>
    public static void Mix(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination,
        int channels)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (left.Length != right.Length || left.Length != destination.Length)
        {
            throw new ArgumentException("Crossfadeする左右バッファと出力バッファの長さは一致する必要があります。");
        }

        if (left.Length % channels != 0)
        {
            throw new ArgumentException("バッファ長はチャンネル数の倍数である必要があります。");
        }

        var frames = left.Length / channels;
        if (frames == 0)
        {
            return;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            // 端点そのものを0/1にすると片側が完全に消えるため、N+1分割の内部点を用いる。
            var rightWeight = (frame + 1d) / (frames + 1d);
            var leftWeight = 1d - rightWeight;
            var offset = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                destination[offset + channel] = (float)(
                    (left[offset + channel] * leftWeight)
                    + (right[offset + channel] * rightWeight));
            }
        }
    }
}
