using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeechの利用者向け論理モデル選択をprecision別の物理package IDから分離して公開する
/// </summary>
public sealed class ReazonSpeechModelSelectionCapability : ITranscriptionModelSelectionCapability
{
    /// <inheritdoc />
    public TranscriptionModelId GetSelectedModel(ITranscriptionEngineOptions options)
        => options is ReazonSpeechEngineOptions reazon
            ? reazon.ModelId
            : throw new ArgumentException("ReazonSpeech以外のEngine optionsが渡されました。", nameof(options));
}
