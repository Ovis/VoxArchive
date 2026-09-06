using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription;

/// <summary>
/// 元録音をEngineが要求する認識用音声へ正規化する
/// </summary>
public sealed class TranscriptionAudioPreparationService
{
    private const float TranscriptionSafePeak = 0.98f;

    /// <summary>
    /// 元録音をEngine要求に従って前処理する
    /// </summary>
    /// <param name="audioFilePath">元録音ファイル</param>
    /// <param name="speakerGainDb">録音CH1へ適用するゲイン</param>
    /// <param name="micGainDb">録音CH2へ適用するゲイン</param>
    /// <param name="requirements">Engineが宣言した入力要件</param>
    /// <param name="cancellationToken">処理のキャンセルを通知するトークン</param>
    public async Task<IPreparedTranscriptionAudio> PrepareAsync(
        string audioFilePath,
        double speakerGainDb,
        double micGainDb,
        TranscriptionAudioRequirements requirements,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(requirements);
        ValidateRequirements(requirements);

        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-transcription-{Guid.NewGuid():N}.wav");
        try
        {
            var duration = await Task.Run(
                () => ConvertAudioToWaveFile(audioFilePath, tempWavePath, speakerGainDb, micGainDb, requirements, cancellationToken),
                cancellationToken);
            return new PreparedTranscriptionAudio(tempWavePath, requirements, duration);
        }
        catch
        {
            TryDelete(tempWavePath);
            throw;
        }
    }

    private static TimeSpan ConvertAudioToWaveFile(
        string sourcePath,
        string destinationPath,
        double speakerGainDb,
        double micGainDb,
        TranscriptionAudioRequirements requirements,
        CancellationToken cancellationToken)
    {
        var tempRawWavePath = destinationPath + ".raw.tmp";
        try
        {
            using var reader = new AudioFileReader(sourcePath);
            var sourceDuration = reader.TotalTime;
            var sampleProvider = BuildTranscriptionSampleProvider(reader, speakerGainDb, micGainDb, requirements);
            var firstPassPeak = WriteSampleProviderAsPcm16Wave(sampleProvider, tempRawWavePath, 1f, cancellationToken);
            if (firstPassPeak <= TranscriptionSafePeak)
            {
                File.Move(tempRawWavePath, destinationPath, true);
                return sourceDuration;
            }

            // ゲイン適用後のピークが安全域を超えた場合だけ全体を減衰する。
            // 常時正規化すると従来のレベル感が変わるため、既存挙動と同じくクリッピング回避時に限定する。
            var safeScale = (float)Math.Clamp(TranscriptionSafePeak / firstPassPeak, 0f, 1f);
            using var normalizationReader = new AudioFileReader(tempRawWavePath);
            WriteSampleProviderAsPcm16Wave(normalizationReader, destinationPath, safeScale, cancellationToken);
            File.Delete(tempRawWavePath);
            return sourceDuration;
        }
        finally
        {
            TryDelete(tempRawWavePath);
        }
    }

    private static ISampleProvider BuildTranscriptionSampleProvider(
        ISampleProvider source,
        double speakerGainDb,
        double micGainDb,
        TranscriptionAudioRequirements requirements)
    {
        var provider = source;
        var speakerGain = (float)Math.Clamp(DbToLinearGain(speakerGainDb), 0.01d, 8d);
        var micGain = (float)Math.Clamp(DbToLinearGain(micGainDb), 0.01d, 8d);

        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f * speakerGain,
                RightVolume = 0.5f * micGain
            };
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            // VoxArchive録音の先頭2ch=SPK/MICという既存仕様を維持する。
            // speaker labelingは別途original audioを読むため、ここではASR入力だけをmixdownする。
            var firstTwoChannels = new MultiplexingSampleProvider(new[] { provider }, 2);
            firstTwoChannels.ConnectInputToOutput(0, 0);
            firstTwoChannels.ConnectInputToOutput(1, 1);
            provider = new StereoToMonoSampleProvider(firstTwoChannels)
            {
                LeftVolume = 0.5f * speakerGain,
                RightVolume = 0.5f * micGain
            };
        }
        else
        {
            var monoGain = (float)Math.Clamp(DbToLinearGain((speakerGainDb + micGainDb) / 2d), 0.01d, 8d);
            if (Math.Abs(monoGain - 1f) > 0.0001f)
            {
                provider = new VolumeSampleProvider(provider) { Volume = monoGain };
            }
        }

        if (provider.WaveFormat.SampleRate != requirements.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, requirements.SampleRate);
        }

        if (provider.WaveFormat.Channels != requirements.Channels)
        {
            throw new NotSupportedException(
                $"現在の共通Audio Preparationは{requirements.Channels}chへの変換をサポートしていません。実際={provider.WaveFormat.Channels}ch");
        }

        return provider;
    }

    private static float WriteSampleProviderAsPcm16Wave(
        ISampleProvider provider,
        string destinationPath,
        float outputScale,
        CancellationToken cancellationToken)
    {
        var peak = 0f;
        var sampleBuffer = new float[Math.Max(4096, provider.WaveFormat.SampleRate / 2)];
        var pcmBuffer = new byte[sampleBuffer.Length * 2];
        using var writer = new WaveFileWriter(
            destinationPath,
            new WaveFormat(provider.WaveFormat.SampleRate, 16, provider.WaveFormat.Channels));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(sampleBuffer.AsSpan());
            if (read <= 0)
            {
                break;
            }

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

    private static void ValidateRequirements(TranscriptionAudioRequirements requirements)
    {
        if (requirements.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requirements), "SampleRateは正数である必要があります。");
        }

        // 現行Whisper/ReazonSpeechはいずれもmono/PCM16を要求する。
        // 将来Engineが別形式を要求した際は、共通契約を固定せずここへ変換戦略を追加する。
        if (requirements.Channels != 1 || requirements.SampleFormat != TranscriptionSampleFormat.Pcm16)
        {
            throw new NotSupportedException(
                $"未対応の文字起こし音声要件です: {requirements.SampleRate}Hz/{requirements.Channels}ch/{requirements.SampleFormat}");
        }
    }

    private static double DbToLinearGain(double gainDb) => Math.Pow(10d, gainDb / 20d);

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
            // 失敗時の掃除はbest effortとする。一時ファイル名はGUIDなので後続Jobへ影響しない。
        }
    }
}
