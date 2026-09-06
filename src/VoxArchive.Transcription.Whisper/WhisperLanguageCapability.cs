using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisperの言語指定能力をCommonへ公開する
/// </summary>
public sealed class WhisperLanguageCapability : ITranscriptionLanguageCapability
{
    /// <inheritdoc />
    public bool Supports(string? preferredLanguage)
    {
        // Whisperは空指定を自動言語判定として扱える。
        // 実在しない言語コードの厳密検証はWhisper側catalog導入時に追加し、Commonへ言語一覧を埋め込まない。
        return preferredLanguage is null || !preferredLanguage.Contains(char.IsWhiteSpace);
    }
}
