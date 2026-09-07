using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Silero VADを優先し、利用できない場合や推論失敗時に音量ベースVADへ切り替える
/// </summary>
/// <remarks>
/// 同一ジョブ内でSileroの途中結果とfallback結果を混在させないため、Silero失敗時は結果をすべて破棄し、
/// 同じPrepared Audioをfallback detectorで先頭から再解析する。Sileroの正常な0件結果は「無音」としてそのまま返す。
/// </remarks>
public sealed class SileroPreferredSpeechRegionDetector : ISpeechRegionDetector
{
    private const int ConsecutiveInferenceFailureLimit = 3;
    private readonly ISpeechRegionDetector _sileroDetector;
    private readonly ISpeechRegionDetector _fallbackDetector;
    private readonly object _stateLock = new();
    private int _consecutiveInferenceFailures;
    private bool _sileroSuppressedForSession;

    /// <summary>
    /// Silero detectorとfallback detectorを指定して初期化する
    /// </summary>
    /// <param name="sileroDetector">Silero VADを実行するdetector</param>
    /// <param name="fallbackDetector">Sileroを利用できない場合に先頭から再実行する音量ベースVAD</param>
    public SileroPreferredSpeechRegionDetector(
        ISpeechRegionDetector sileroDetector,
        ISpeechRegionDetector fallbackDetector)
    {
        _sileroDetector = sileroDetector ?? throw new ArgumentNullException(nameof(sileroDetector));
        _fallbackDetector = fallbackDetector ?? throw new ArgumentNullException(nameof(fallbackDetector));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsSileroSuppressed())
        {
            return await RunFallbackAsync(audio, settings, cancellationToken);
        }

        try
        {
            var regions = await _sileroDetector.DetectAsync(audio, settings, cancellationToken);

            // 0件もSileroが正常終了した結果なので成功として扱う。
            // 以前の推論失敗を引きずると無音ジョブの後も不要なsession suppressionへ近づくため、ここで連続回数を戻す。
            ResetInferenceFailures();
            return regions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ユーザーキャンセルはSilero障害ではないため、fallbackも失敗回数更新も行わない。
            throw;
        }
        catch (SileroVadUnavailableException)
        {
            // 未配置・破損・初期化失敗はモデル利用不可としてfallbackするが、
            // 「3回連続した推論失敗」によるsession suppressionの対象には含めない。
            return await RunFallbackAsync(audio, settings, cancellationToken);
        }
        catch
        {
            RecordInferenceFailure();

            // Silero側で途中までregionを得ていても採用せず、同一Prepared Audioをfallbackで最初から解析する。
            // fallbackまで失敗した場合はその例外を上位へ伝え、VADなしでASRへ進ませない。
            return await RunFallbackAsync(audio, settings, cancellationToken);
        }
    }

    private Task<IReadOnlyList<SpeechRegion>> RunFallbackAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken)
        => _fallbackDetector.DetectAsync(audio, settings, cancellationToken);

    private bool IsSileroSuppressed()
    {
        lock (_stateLock)
        {
            return _sileroSuppressedForSession;
        }
    }

    private void ResetInferenceFailures()
    {
        lock (_stateLock)
        {
            _consecutiveInferenceFailures = 0;
        }
    }

    private void RecordInferenceFailure()
    {
        lock (_stateLock)
        {
            _consecutiveInferenceFailures++;
            if (_consecutiveInferenceFailures >= ConsecutiveInferenceFailureLimit)
            {
                // suppressionはプロセス内状態にだけ保持する。アプリ再起動時には再度Sileroを試す仕様のため永続化しない。
                _sileroSuppressedForSession = true;
            }
        }
    }
}
