using System.Text.Json;

namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// VADが直接検出したpadding/merge前の発話範囲を表す
/// </summary>
/// <param name="RawSpeechRegionId">1回のVAD実行内で採番するraw region ID</param>
/// <param name="StartSample">Prepared Audio上の開始sample</param>
/// <param name="EndSample">Prepared Audio上の終了sample</param>
public sealed record SpeechRegionDetectionRawRegion(
    int RawSpeechRegionId,
    long StartSample,
    long EndSample);

/// <summary>
/// 1回のVAD実行で確定したdetector選択、実際の実行設定、fallback情報を保持する
/// </summary>
/// <remarks>
/// この型は診断JSONそのものではなく、Commonとdetector実装の間で受け渡す実行traceである。
/// Silero固有型をCommonへ漏らさず、同じ呼び出しの結果として返すことでsingleton detectorでもJob間のtrace混同を防ぐ。
/// EffectiveSettingsは要求されたSilero設定ではなく、そのDetectorが実際の判定に使用した値をopaque JSONで保持する。
/// </remarks>
public sealed record SpeechRegionDetectionDiagnosticTrace(
    string Detector,
    IReadOnlyList<SpeechRegionDetectionRawRegion> RawRegions,
    bool FallbackUsed,
    string? FallbackReason,
    JsonElement? EffectiveSettings = null);

/// <summary>
/// VADの最終SpeechRegionと、その生成過程を説明する診断traceをまとめて返す
/// </summary>
public sealed record SpeechRegionDetectionDiagnosticResult(
    IReadOnlyList<SpeechRegion> SpeechRegions,
    SpeechRegionDetectionDiagnosticTrace Trace);

/// <summary>
/// 詳細診断が有効なJobで、VAD結果と診断traceを同時に取得できるdetector契約
/// </summary>
public interface IDiagnosticSpeechRegionDetector : ISpeechRegionDetector
{
    /// <summary>
    /// 通常のSpeechRegionに加えてraw region、detector種別、実際の実行設定、fallback理由を返す
    /// </summary>
    Task<SpeechRegionDetectionDiagnosticResult> DetectWithDiagnosticsAsync(
        IPreparedTranscriptionAudio audio,
        SpeechRegionDetectorSettingsSnapshot settings,
        CancellationToken cancellationToken = default);
}
