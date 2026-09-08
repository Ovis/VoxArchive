using System.Text.Json;
using Microsoft.Extensions.Logging;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// ユーザーが選択した発話検出方式に従い、Silero選択時だけ必要に応じて音量ベースVADへfallbackする
/// </summary>
/// <remarks>
/// 同一ジョブ内でSileroの途中結果とfallback結果を混在させないため、Silero失敗時は結果をすべて破棄し、
/// 同じPrepared Audioをfallback detectorで先頭から再解析する。Sileroの正常な0件結果は「無音」としてそのまま返す。
/// 音量ベースVADを明示選択した場合はSileroのモデル状態に関係なくnative処理を一切試行しない。
/// </remarks>
public sealed class SileroPreferredSpeechRegionDetector(
    ISpeechRegionDetector sileroDetector,
    ISpeechRegionDetector fallbackDetector,
    ILogger<SileroPreferredSpeechRegionDetector> logger,
    ITranscriptionWarningSink? warningSink = null) : IDiagnosticSpeechRegionDetector
{
    private const int ConsecutiveInferenceFailureLimit = 3;
    private readonly ISpeechRegionDetector _sileroDetector = sileroDetector ?? throw new ArgumentNullException(nameof(sileroDetector));
    private readonly ISpeechRegionDetector _fallbackDetector = fallbackDetector ?? throw new ArgumentNullException(nameof(fallbackDetector));
    private readonly ILogger<SileroPreferredSpeechRegionDetector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITranscriptionWarningSink? _warningSink = warningSink;
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

        if (UseVolumeBasedDetector(settings))
        {
            // 明示選択はSilero障害によるfallbackではない。native model loadやsession suppression判定にも触れず、
            // 診断上も通常のVolumeBasedVadとして記録する。
            return await RunVolumeBasedAsync(audio, settings, includeDiagnostics, cancellationToken);
        }

        if (IsSileroSuppressed())
        {
            _logger.LogInformation("Silero VAD is suppressed for this application session. Falling back to volume-based VAD.");
            ReportWarning("silero-session-suppressed");
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
            ReportWarning("silero-unavailable");
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
            ReportWarning(failureCount >= ConsecutiveInferenceFailureLimit
                ? "silero-inference-failed-session-suppressed"
                : "silero-inference-failed");

            // Silero側で途中までregionを得ていても採用せず、同一Prepared Audioをfallbackで最初から解析する。
            // fallbackまで失敗した場合はその例外を上位へ伝え、VADなしでASRへ進ませない。
            return await RunFallbackAsync(audio, settings, "silero-inference-failed", includeDiagnostics, cancellationToken);
        }
    }

    /// <summary>
    /// Admissionで固定されたsnapshotから音量ベースVADの明示選択を判定する
    /// </summary>
    /// <remarks>
    /// PR #27以前の設定にはModeが存在しないため、未指定・読取不能な値は従来動作のSileroとして扱う。
    /// System.Text.Jsonの既定enum表現は数値だが、将来serializer設定が変わっても旧設定を壊さないよう文字列も受け付ける。
    /// </remarks>
    internal static bool UseVolumeBasedDetector(SpeechRegionDetectorSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !settings.Settings.TryGetProperty("Mode", out var mode))
        {
            return false;
        }

        if (mode.ValueKind == JsonValueKind.Number && mode.TryGetInt32(out var numericMode))
        {
            return numericMode == 1;
        }

        return mode.ValueKind == JsonValueKind.String
            && string.Equals(mode.GetString(), "VolumeBased", StringComparison.OrdinalIgnoreCase);
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

    private async Task<SpeechRegionDetectionDiagnosticResult> RunVolumeBasedAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        if (includeDiagnostics && _fallbackDetector is IDiagnosticSpeechRegionDetector diagnosticDetector)
        {
            return await diagnosticDetector.DetectWithDiagnosticsAsync(audio, settings, cancellationToken);
        }

        var regions = await _fallbackDetector.DetectAsync(audio, settings, cancellationToken);
        return new SpeechRegionDetectionDiagnosticResult(
            regions,
            new SpeechRegionDetectionDiagnosticTrace(
                "VolumeBasedVad",
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
        var fallbackResult = await RunVolumeBasedAsync(audio, settings, includeDiagnostics, cancellationToken);

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

    private void ReportWarning(string code)
    {
        if (_warningSink is null)
        {
            return;
        }

        try
        {
            // UI通知失敗で文字起こし本体を失敗させない。通常ログと詳細診断が一次情報として残る。
            _warningSink.Report(new TranscriptionWarning(code));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report non-blocking transcription warning. Code={Code}", code);
        }
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
