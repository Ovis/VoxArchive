using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeechで従来利用してきたartifact file nameを維持する
/// </summary>
public sealed class ReazonSpeechArtifactNamingCapability : ITranscriptionArtifactNamingCapability
{
    /// <inheritdoc />
    public string BuildFileNameSuffix(TranscriptionModelId? modelId)
    {
        if (modelId is null)
        {
            throw new InvalidOperationException("ReazonSpeech artifact naming requires a model ID.");
        }

        // ReazonSpeech導入時から <録音名>-reazonspeech-ja.json を使用しているため、
        // CommonへEngine名をhard-codeせずEngine capability側で既存規則を維持する。
        return $"reazonspeech-{modelId.Value.Value}";
    }
}
