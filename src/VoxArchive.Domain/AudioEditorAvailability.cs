namespace VoxArchive.Domain;

/// <summary>
/// Audio Editorを新規起動できるかをアプリ状態から判定する。
/// </summary>
/// <remarks>
/// 現行のRecording Serviceは録音中ファイルのパスを公開していないため、録音中は安全側に倒して
/// 新しいEditorの起動自体を禁止する。既に開いているEditorの編集セッションまでは停止しない。
/// </remarks>
public static class AudioEditorAvailability
{
    /// <summary>
    /// 新しいAudio Editorを起動できるかを返す。
    /// </summary>
    public static bool CanOpen(RecordingState recordingState, bool isExporting)
        => recordingState == RecordingState.Stopped && !isExporting;

    /// <summary>
    /// 起動できない理由をUI表示用に返す。起動可能ならnull。
    /// </summary>
    public static string? GetUnavailableReason(RecordingState recordingState, bool isExporting)
    {
        if (isExporting)
        {
            return "別の音声を書き出し中のため、新しいAudio Editorは開けません。";
        }

        return recordingState == RecordingState.Stopped
            ? null
            : "録音処理中は新しいAudio Editorを開けません。録音を停止してからもう一度実行してください。";
    }
}
