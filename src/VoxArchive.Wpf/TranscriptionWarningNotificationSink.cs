using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// 文字起こし基盤の継続可能な警告をWindowsの非ブロッキング通知へ投影する
/// </summary>
/// <remarks>
/// Transcription層は安定codeだけを通知し、利用者向け文言と通知手段はPresentation層で決める。
/// session suppression中は同じ警告が各ジョブで繰り返されるため、その状態に入った通知はプロセス内で1回に抑える。
/// </remarks>
internal sealed class TranscriptionWarningNotificationSink : ITranscriptionWarningSink
{
    private readonly object _gate = new();
    private bool _sessionSuppressedNotified;

    /// <inheritdoc />
    public void Report(TranscriptionWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        var message = warning.Code switch
        {
            "silero-unavailable" => "Silero VADを利用できないため、音量ベースVADで文字起こしを続行します。設定画面でモデル状態を確認してください。",
            "silero-inference-failed" => "Silero VADの処理に失敗したため、音量ベースVADで文字起こしを続行します。",
            "silero-inference-failed-session-suppressed" => BuildSessionSuppressedMessageOnce(
                "Silero VADの処理が3回連続で失敗したため、この起動中は音量ベースVADを使用します。アプリ再起動後はSilero VADを再試行します。"),
            "silero-session-suppressed" => BuildSessionSuppressedMessageOnce(
                "Silero VADはこの起動中無効化されているため、音量ベースVADで文字起こしを続行します。"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppNotificationHub.Notify("VoxArchive", message, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private string? BuildSessionSuppressedMessageOnce(string message)
    {
        lock (_gate)
        {
            if (_sessionSuppressedNotified)
            {
                return null;
            }

            _sessionSuppressedNotified = true;
            return message;
        }
    }
}
