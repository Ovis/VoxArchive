namespace VoxArchive.Transcription.Abstractions;

/// <summary>
/// 文字起こしpipelineを失敗させず利用者へ知らせるべき警告の通知先を定義する
/// </summary>
/// <remarks>
/// Engine/VAD実装からWPFの通知機構を直接参照しないための境界である。
/// 警告通知自体の失敗で文字起こし結果を変えないことを前提とする。
/// </remarks>
public interface ITranscriptionWarningSink
{
    /// <summary>文字起こしを継続可能な警告を通知する</summary>
    /// <param name="warning">Presentation層で表示文言へ変換できる安定codeを持つ警告</param>
    void Report(TranscriptionWarning warning);
}

/// <summary>
/// 文字起こしを継続しながら利用者へ知らせる警告を表す
/// </summary>
/// <param name="Code">Presentation層で表示内容を決める安定code</param>
public sealed record TranscriptionWarning(string Code);
