using Microsoft.Extensions.Logging;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Silero VADを優先し、利用できない場合や推論失敗時に音量ベースVADへ切り替える
/// </summary>
/// <remarks>
/// 同一ジョブ内でSileroの途中結果とfallback結果を混在させないため、Silero失敗時は結果をすべて破棄し、
/// 同じPrepared Audioをfallback detectorで先頭から再解析する。Sileroの正常な0件結果は「無音」としてそのまま返す。
/// </remarks>
public sealed class SileroPreferredSpeechRegionDetector(
    ISpeechRegionDetector sileroDetector,
    ISpeechRegionDetector fallbackDetector,
    ILogger<SileroPreferredSpeechRegionDetector> logger) : IDiagnosticSpeechRegionDetector
{
    private const int ConsecutiveInferenceFailureLimit = 3;
    private readonly ISpeechRegionDetector _sileroDetector = sileroDetector ?? throw new ArgumentNullException(nameof(sileroDetector));
    private readonly ISpeechRegionDetector _fallbackDetector = fallbackDetector ?? throw new ArgumentNullException(nameof(fallbackDetector));
    private readonly ILogger<SileroPreferredSpeechRegionDetector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly object _stateLock = new();
    private int _consecutiveInferenceFailures;
    private bool _sileroSuppressedForSession;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
        => (await DetectCoreAsync(audio, settings, includeDiagnostics: false, cancellationToken)).SpeechRegions;

    /// <inheritdoc />
    public Task<SpeechRegionDetectionDiagnosticResult> DetectWithDiagnosticsAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
        => DetectCoreAsync(audio, settings, includeDiagnostics: true, cancellationToken);

    private async Task<SpeechRegionDetectionDiagnosticResult> DetectCoreAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsSileroSuppressed())
        {
            _logger.LogInformation("Silero VAD is suppressed for this application session. Falling back to volume-based VAD.");
            return await RunFallbackAsync(audio, settings, "session-suppressed", includeDiagnostics, cancellationToken);
        }

        try
        {
            var result = await RunSileroAsync(audio, settings, includeDiagnostics, cancellationToken);

            // 0件もSileroが正常終了した結果なので成功として扱う。
            // 以前の推論失敗を引きずると無音ジョブの後も不要なsession suppressionへ近づくため、ここで連続回数を戻す。
            ResetInferenceFailures();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ユーザーキャンセルはSilero障害ではないため、fallbackも失敗回数更新も行わない。
            throw;
        }
        catch (SileroVadUnavailableException ex)
        {
            // 未配置・破損・初期化失敗はモデル利用不可としてfallbackするが、
            // 「3回連続した推論失敗」によるsession suppressionの対象には含めない。
            _logger.LogWarning(ex, "Silero VAD is unavailable. Falling back to volume-based VAD.");
            return await RunFallbackAsync(audio, settings, "silero-unavailable", includeDiagnostics, cancellationToken);
        }
        catch (Exception ex)
        {
            var failureCount = RecordInferenceFailure();
            _logger.LogWarning(
                ex,
                "Silero VAD inference failed. Falling back to volume-based VAD. ConsecutiveFailures={ConsecutiveFailures} SuppressedForSession={SuppressedForSession}",
                failureCount,
                failureCount >= ConsecutiveInferenceFailureLimit);

            // Silero側で途中までregionを得ていても採用せず、同一Prepared Audioをfallbackで最初から解析する。
            // fallbackまで失敗した場合はその例外を上位へ伝え、VADなしでASRへ進ませない。
            return await RunFallbackAsync(audio, settings, "silero-inference-failed", includeDiagnostics, cancellationToken);
        }
    }

    private async Task<SpeechRegionDetectionDiagnosticResult> RunSileroAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        if (includeDiagnostics && _sileroDetector is IDiagnosticSpeechRegionDetector diagnosticDetector)
        {
            return await diagnosticDetector.DetectWithDiagnosticsAsync(audio, settings, cancellationToken);
        }

        var regions = await _sileroDetector.DetectAsync(audio, settings, cancellationToken);
        return new SpeechRegionDetectionDiagnosticResult(
            regions,
            new SpeechRegionDetectionDiagnosticTrace(
                "SileroVad",
                [],
                FallbackUsed: false,
                FallbackReason: null));
    }

    private async Task<SpeechRegionDetectionDiagnosticResult> RunFallbackAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        string reason,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        SpeechRegionDetectionDiagnosticResult fallbackResult;
        if (includeDiagnostics && _fallbackDetector is IDiagnosticSpeechRegionDetector diagnosticDetector)
        {
            fallbackResult = await diagnosticDetector.DetectWithDiagnosticsAsync(audio, settings, cancellationToken);
        }
        else
        {
            var regions = await _fallbackDetector.DetectAsync(audio, settings, cancellationToken);
            fallbackResult = new SpeechRegionDetectionDiagnosticResult(
                regions,
                new SpeechRegionDetectionDiagnosticTrace(
                    "VolumeBasedVad",
                    [],
                    FallbackUsed: false,
                    FallbackReason: null));
        }

        // fallback detector自身のtraceを尊重しつつ、今回Sileroから切り替わった事実と理由を上書きする。
        // これにより将来volume VAD側がraw region診断へ対応しても、その情報を失わない。
        return fallbackResult with
        {
            Trace = fallbackResult.Trace with
            {
                FallbackUsed = true,
                FallbackReason = reason
            }
        };
    }

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

    private int RecordInferenceFailure()
    {
        lock (_stateLock)
        {
            _consecutiveInferenceFailures++;
            if (_consecutiveInferenceFailures >= ConsecutiveInferenceFailureLimit)
            {
                // suppressionはプロセス内状態にだけ保持する。アプリ再起動時には再度Sileroを試す仕様のため永続化しない。
                _sileroSuppressedForSession = true;
            }
            return _consecutiveInferenceFailures;
        }
    }
}
