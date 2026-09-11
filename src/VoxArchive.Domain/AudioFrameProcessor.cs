namespace VoxArchive.Domain;

/// <summary>
/// 1サンプルフレームへGain・Mute・チャンネル構成・Master Gainを適用する純粋DSP処理
/// </summary>
/// <remarks>
/// ここではクリップしない。Clipping判定と自動Master減衰は、この処理後の値を解析して別段階で行う。
/// Mono Mixdownは仕様どおり単純加算とし、暗黙の1/2減衰は行わない。
/// </remarks>
public static class AudioFrameProcessor
{
    public static int GetOutputChannelCount(int inputChannelCount, AudioRenderChannelMode channelMode)
    {
        if (inputChannelCount is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(inputChannelCount));
        }

        return channelMode == AudioRenderChannelMode.MonoMixdown || inputChannelCount == 1 ? 1 : 2;
    }

    /// <summary>
    /// 1フレームを処理する
    /// </summary>
    /// <param name="input">入力チャンネル数分のサンプル</param>
    /// <param name="output">出力チャンネル数以上の領域</param>
    /// <param name="channelStates">入力チャンネルごとのGain・Mute</param>
    /// <param name="channelMode">出力チャンネル構成</param>
    /// <param name="masterGainDb">最終段で適用するMaster Gain</param>
    /// <returns>書き込んだ出力チャンネル数</returns>
    public static int ProcessFrame(
        ReadOnlySpan<float> input,
        Span<float> output,
        IReadOnlyList<AudioChannelEditState> channelStates,
        AudioRenderChannelMode channelMode,
        double masterGainDb = 0d)
    {
        ArgumentNullException.ThrowIfNull(channelStates);
        if (input.Length is < 1 or > 2)
        {
            throw new ArgumentException("入力はMonoまたはStereoの1フレームである必要があります。", nameof(input));
        }

        if (channelStates.Count != input.Length)
        {
            throw new ArgumentException("チャンネル編集状態数は入力チャンネル数と一致する必要があります。", nameof(channelStates));
        }

        if (double.IsNaN(masterGainDb) || double.IsPositiveInfinity(masterGainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(masterGainDb));
        }

        var outputChannels = GetOutputChannelCount(input.Length, channelMode);
        if (output.Length < outputChannels)
        {
            throw new ArgumentException("出力領域が不足しています。", nameof(output));
        }

        Span<double> adjusted = stackalloc double[2];
        for (var channel = 0; channel < input.Length; channel++)
        {
            var state = channelStates[channel];
            adjusted[channel] = state.IsMuted ? 0d : input[channel] * state.ToLinearGain();
        }

        var masterGain = double.IsNegativeInfinity(masterGainDb)
            ? 0d
            : Math.Pow(10d, masterGainDb / 20d);

        if (outputChannels == 1)
        {
            var mixed = input.Length == 1 ? adjusted[0] : adjusted[0] + adjusted[1];
            output[0] = (float)(mixed * masterGain);
            return 1;
        }

        output[0] = (float)(adjusted[0] * masterGain);
        output[1] = (float)(adjusted[1] * masterGain);
        return 2;
    }
}
