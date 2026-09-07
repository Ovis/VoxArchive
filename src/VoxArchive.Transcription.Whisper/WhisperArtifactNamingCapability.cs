using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisperで従来利用してきたartifact file nameを維持する
/// </summary>
public sealed class WhisperArtifactNamingCapability : ITranscriptionArtifactNamingCapability
{
    /// <inheritdoc />
    public string BuildFileNameSuffix(TranscriptionModelId? modelId)
    {
        if (modelId is null)
        {
            throw new InvalidOperationException("Whisper artifact naming requires a model ID.");
        }

        // 既存VoxArchiveは <録音名>-small.json のようにmodel IDだけをsuffixとしていた。
        // リファクタリングでファイル名が変わるとLibrary上で過去結果と新規結果が分断されるため維持する。
        return modelId.Value.Value;
    }
}
