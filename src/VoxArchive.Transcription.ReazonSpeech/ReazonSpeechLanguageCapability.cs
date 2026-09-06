using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// 日本語固定モデルであるReazonSpeechの言語能力を公開する
/// </summary>
public sealed class ReazonSpeechLanguageCapability : ITranscriptionLanguageCapability
{
    /// <inheritdoc />
    public bool Supports(string? preferredLanguage)
    {
        // 空指定は「利用者が言語を限定していない」ため受理する。
        // 日本語以外を指定した場合は暗黙に日本語認識へfallbackせずAdmissionで拒否する。
        return string.IsNullOrWhiteSpace(preferredLanguage)
               || string.Equals(preferredLanguage.Trim(), "ja", StringComparison.OrdinalIgnoreCase)
               || string.Equals(preferredLanguage.Trim(), "ja-JP", StringComparison.OrdinalIgnoreCase);
    }
}
