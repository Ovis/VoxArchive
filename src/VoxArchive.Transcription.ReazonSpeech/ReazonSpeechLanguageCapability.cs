using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// 日本語固定モデルであるReazonSpeechの言語能力を公開する
/// </summary>
public sealed class ReazonSpeechLanguageCapability : ITranscriptionLanguageCapability
{
    /// <inheritdoc />
    public bool Supports(string? preferredLanguage)
        => string.IsNullOrWhiteSpace(preferredLanguage)
           || string.Equals(preferredLanguage.Trim(), "ja", StringComparison.OrdinalIgnoreCase)
           || string.Equals(preferredLanguage.Trim(), "ja-JP", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ITranscriptionEngineOptions Resolve(
        ITranscriptionEngineOptions options,
        string? preferredLanguage)
    {
        if (options is not ReazonSpeechEngineOptions reazon)
        {
            throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));
        }
        if (!Supports(preferredLanguage))
        {
            // 日本語固定Engineへ別言語を指定した場合に黙って日本語認識しない。
            throw new NotSupportedException($"ReazonSpeechで利用できないPreferredLanguageです: {preferredLanguage}");
        }
        return reazon;
    }
}
