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
        // 空指定はWhisperの自動言語判定として扱う。
        // 空白を含む値は言語コードとして成立しないため、暗黙にautoへfallbackさせない。
        return preferredLanguage is null || !preferredLanguage.Any(char.IsWhiteSpace);
    }

    /// <inheritdoc />
    public ITranscriptionEngineOptions Resolve(
        ITranscriptionEngineOptions options,
        string? preferredLanguage)
    {
        if (options is not WhisperEngineOptions whisper)
        {
            throw new ArgumentException("Whisper以外のEngine optionsが渡されました。", nameof(options));
        }
        if (!Supports(preferredLanguage))
        {
            throw new NotSupportedException($"Whisperで利用できないPreferredLanguageです: {preferredLanguage}");
        }
        return whisper with { Language = preferredLanguage?.Trim() ?? string.Empty };
    }
}
