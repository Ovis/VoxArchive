namespace VoxArchive.Domain;

/// <summary>
/// Audio EditorでUndo/RedoおよびDirty判定の対象となる軽量な編集状態を保持する
/// </summary>
/// <remarks>
/// PCMや波形などの大きなデータは保持せず、CutRange・Gain・Muteだけを値として保持する。
/// CutRangeは生成時に正規化し、状態同士の比較が編集内容の比較としてそのまま利用できるようにする。
/// </remarks>
public sealed class AudioEditState : IEquatable<AudioEditState>
{
    private readonly AudioCutRange[] _cutRanges;
    private readonly AudioChannelEditState[] _channels;

    /// <summary>
    /// 編集状態を生成する
    /// </summary>
    /// <param name="sourceDuration">元音声の長さ</param>
    /// <param name="channels">入力音声のチャンネル数。MonoまたはStereoのみを許可する</param>
    /// <param name="cutRanges">元音声時間軸上のCutRange</param>
    /// <param name="channelStates">チャンネルごとのGain・Mute状態。省略時は全チャンネル0dB・Mute解除</param>
    public AudioEditState(
        TimeSpan sourceDuration,
        int channels,
        IEnumerable<AudioCutRange>? cutRanges = null,
        IEnumerable<AudioChannelEditState>? channelStates = null)
    {
        if (sourceDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        }

        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Audio EditorはMonoまたはStereoのみを扱います。");
        }

        SourceDuration = sourceDuration;
        ChannelCount = channels;
        _cutRanges = AudioCutRangeNormalizer.Normalize(cutRanges ?? Array.Empty<AudioCutRange>()).ToArray();
        if (_cutRanges.Any(x => x.End > sourceDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(cutRanges), "CutRangeは元音声の範囲内である必要があります。");
        }

        _channels = channelStates?.ToArray()
            ?? Enumerable.Repeat(AudioChannelEditState.Default, channels).ToArray();
        if (_channels.Length != channels)
        {
            throw new ArgumentException("チャンネル編集状態数は入力音声のチャンネル数と一致する必要があります。", nameof(channelStates));
        }
    }

    /// <summary>
    /// 元音声の長さ
    /// </summary>
    public TimeSpan SourceDuration { get; }

    /// <summary>
    /// 入力音声のチャンネル数
    /// </summary>
    public int ChannelCount { get; }

    /// <summary>
    /// 正規化済みCutRange
    /// </summary>
    public IReadOnlyList<AudioCutRange> CutRanges => _cutRanges;

    /// <summary>
    /// チャンネルごとのGain・Mute状態
    /// </summary>
    public IReadOnlyList<AudioChannelEditState> Channels => _channels;

    /// <summary>
    /// CutRangeを反映した推定出力時間
    /// </summary>
    public TimeSpan EstimatedOutputDuration
        => SourceDuration - TimeSpan.FromTicks(_cutRanges.Sum(x => x.Duration.Ticks));

    /// <summary>
    /// 少なくとも1チャンネルがMute解除され、かつGainが-∞dBではないか
    /// </summary>
    public bool HasAudibleChannel
        => _channels.Any(x => !x.IsMuted && !double.IsNegativeInfinity(x.GainDb));

    /// <summary>
    /// 書き出す音声が残っているか
    /// </summary>
    public bool HasOutputAudio => EstimatedOutputDuration > TimeSpan.Zero && HasAudibleChannel;

    /// <summary>
    /// CutRangeを追加して正規化した新しい状態を返す
    /// </summary>
    public AudioEditState AddCutRange(AudioCutRange range)
        => WithCutRanges(_cutRanges.Append(range));

    /// <summary>
    /// 指定したCutRangeを取り除いた新しい状態を返す
    /// </summary>
    public AudioEditState RemoveCutRange(AudioCutRange range)
        => WithCutRanges(_cutRanges.Where(x => x != range));

    /// <summary>
    /// CutRange全体を置き換えた新しい状態を返す
    /// </summary>
    public AudioEditState WithCutRanges(IEnumerable<AudioCutRange> ranges)
        => new(SourceDuration, ChannelCount, ranges, _channels);

    /// <summary>
    /// 指定チャンネルのGain・Mute状態を置き換えた新しい状態を返す
    /// </summary>
    public AudioEditState WithChannelState(int channelIndex, AudioChannelEditState channelState)
    {
        if ((uint)channelIndex >= (uint)_channels.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex));
        }

        var channels = (AudioChannelEditState[])_channels.Clone();
        channels[channelIndex] = channelState;
        return new AudioEditState(SourceDuration, ChannelCount, _cutRanges, channels);
    }

    /// <inheritdoc />
    public bool Equals(AudioEditState? other)
    {
        if (other is null)
        {
            return false;
        }

        return SourceDuration == other.SourceDuration
            && ChannelCount == other.ChannelCount
            && _cutRanges.SequenceEqual(other._cutRanges)
            && _channels.SequenceEqual(other._channels);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AudioEditState other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SourceDuration);
        hash.Add(ChannelCount);
        foreach (var range in _cutRanges)
        {
            hash.Add(range);
        }

        foreach (var channel in _channels)
        {
            hash.Add(channel);
        }

        return hash.ToHashCode();
    }
}
