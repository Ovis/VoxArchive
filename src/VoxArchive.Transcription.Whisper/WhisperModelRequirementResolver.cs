using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisper optionsとmanaged modelの関係をEngine project内で解決する
/// </summary>
public sealed class WhisperModelRequirementResolver : ITranscriptionModelRequirementResolver
{
    /// <inheritdoc />
    public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options)
        => GetOptions(options).ModelId;

    /// <inheritdoc />
    public ITranscriptionEngineOptions BindInstallation(
        ITranscriptionEngineOptions options,
        TranscriptionModelInstallation installation)
    {
        var whisper = GetOptions(options);
        if (installation.EngineId != WhisperEngineIdentity.EngineId || installation.ModelId != whisper.ModelId)
        {
            throw new InvalidOperationException("Whisper optionsとモデル配置の識別子が一致しません。");
        }
        return whisper with { ModelPath = installation.PrimaryFile };
    }

    private static WhisperEngineOptions GetOptions(ITranscriptionEngineOptions options)
        => options as WhisperEngineOptions
           ?? throw new ArgumentException("Whisper以外のEngine optionsが渡されました。", nameof(options));
}
