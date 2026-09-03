namespace VoxArchive.Wpf;

/// <summary>
/// Whisperによる文字起こしをエンジン契約へ接続する
/// </summary>
/// <remarks>
/// 既存のWhisper実装には手を加えずアダプターとして包むことで、
/// Engine抽象化の導入と認識処理の変更を同じPRに混在させない。
/// </remarks>
public sealed class WhisperTranscriptionEngine(WhisperTranscriptionService transcriptionService) : ITranscriptionEngine
{
    /// <inheritdoc />
    public Task<TranscriptionJobResult> TranscribeAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken = default)
        => transcriptionService.TranscribeAsync(request, cancellationToken);
}
