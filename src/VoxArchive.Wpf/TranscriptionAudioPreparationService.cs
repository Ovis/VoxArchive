using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こしエンジンへ渡す音声を、16kHz・モノラル・PCM16のWAVEへ正規化する
/// </summary>
internal sealed class TranscriptionAudioPreparationService
{
    private const float TranscriptionSafePeak = 0.98f;

    /// <summary>
    /// 指定した録音ファイルを文字起こし用の一時WAVEへ変換する
    /// </summary>
    public async Task<PreparedTranscriptionAudio> PrepareAsync(string audioFilePath, double speakerGainDb, double micGainDb, CancellationToken cancellationToken)
    {
        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-transcription-{Guid.NewGuid():N}.wav");
        await Task.Run(() => ConvertAudioToWaveFile(audioFilePath, tempWavePath, speakerGainDb, micGainDb), cancellationToken);
        return new PreparedTranscriptionAudio(tempWavePath);
    }

    private static void ConvertAudioToWaveFile(string sourcePath, string destinationPath, double speakerGainDb, double micGainDb)
    {
        var tempRawWavePath = destinationPath + ".raw.tmp";
        try
        {
            using var reader = new AudioFileReader(sourcePath);
            var sampleProvider = BuildTranscriptionSampleProvider(reader, speakerGainDb, micGainDb);
            var firstPassPeak = WriteSampleProviderAsPcm16Wave(sampleProvider, tempRawWavePath, 1f);
            if (firstPassPeak <= TranscriptionSafePeak)
            {
                File.Move(tempRawWavePath, destinationPath, true);
                return;
            }

            // クリッピングを避けるため、安全域を超えた場合だけ2回目の書き出しで全体を減衰する。
            var safeScale = (float)Math.Clamp(TranscriptionSafePeak / firstPassPeak, 0f, 1f);
            using var normalizationReader = new AudioFileReader(tempRawWavePath);
            WriteSampleProviderAsPcm16Wave(normalizationReader, destinationPath, safeScale);
            File.Delete(tempRawWavePath);
        }
        finally
        {
            if (File.Exists(tempRawWavePath)) File.Delete(tempRawWavePath);
        }
    }

    private static ISampleProvider BuildTranscriptionSampleProvider(ISampleProvider source, double speakerGainDb, double micGainDb)
    {
        var provider = source;
        var speakerGain = (float)Math.Clamp(DbToLinearGain(speakerGainDb), 0.01d, 8d);
        var micGain = (float)Math.Clamp(DbToLinearGain(micGainDb), 0.01d, 8d);
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f * speakerGain, RightVolume = 0.5f * micGain };
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            // VoxArchiveの録音では先頭2chをSPK/MICとして扱う既存仕様を維持する。
            var firstTwoChannels = new MultiplexingSampleProvider(new[] { provider }, 2);
            firstTwoChannels.ConnectInputToOutput(0, 0);
            firstTwoChannels.ConnectInputToOutput(1, 1);
            provider = new StereoToMonoSampleProvider(firstTwoChannels) { LeftVolume = 0.5f * speakerGain, RightVolume = 0.5f * micGain };
        }
        else
        {
            var monoGain = (float)Math.Clamp(DbToLinearGain((speakerGainDb + micGainDb) / 2d), 0.01d, 8d);
            if (Math.Abs(monoGain - 1f) > 0.0001f) provider = new VolumeSampleProvider(provider) { Volume = monoGain };
        }
        if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
        if (provider.WaveFormat.Channels != 1) throw new InvalidOperationException($"文字起こし入力はモノラルである必要があります。実際のチャンネル数: {provider.WaveFormat.Channels}");
        return provider;
    }

    private static float WriteSampleProviderAsPcm16Wave(ISampleProvider provider, string destinationPath, float outputScale)
    {
        var peak = 0f;
        var sampleBuffer = new float[Math.Max(4096, provider.WaveFormat.SampleRate / 2)];
        var pcmBuffer = new byte[sampleBuffer.Length * 2];
        using var writer = new WaveFileWriter(destinationPath, new WaveFormat(provider.WaveFormat.SampleRate, 16, 1));
        while (true)
        {
            var read = provider.Read(sampleBuffer, 0, sampleBuffer.Length);
            if (read <= 0) break;
            var offset = 0;
            for (var i = 0; i < read; i++)
            {
                var scaled = sampleBuffer[i] * outputScale;
                peak = Math.Max(peak, Math.Abs(scaled));
                var pcm = (short)Math.Round(Math.Clamp(scaled, -1f, 1f) * short.MaxValue);
                pcmBuffer[offset++] = (byte)(pcm & 0xFF);
                pcmBuffer[offset++] = (byte)((pcm >> 8) & 0xFF);
            }
            writer.Write(pcmBuffer, 0, read * 2);
        }
        return peak;
    }

    private static double DbToLinearGain(double gainDb) => Math.Pow(10d, gainDb / 20d);
}

/// <summary>
/// 文字起こし用に正規化した一時音声ファイルのライフサイクルを管理する
/// </summary>
internal sealed class PreparedTranscriptionAudio(string waveFilePath) : IAsyncDisposable
{
    public string WaveFilePath { get; } = waveFilePath;

    /// <summary>
    /// このインスタンスが所有する一時WAVEファイルを削除する
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (File.Exists(WaveFilePath))
        {
            try { File.Delete(WaveFilePath); }
            catch
            {
                // 一時ファイル削除失敗で文字起こし結果まで失敗扱いにしない。GUID名なので後続ジョブとも衝突しない。
            }
        }
        return ValueTask.CompletedTask;
    }
}
