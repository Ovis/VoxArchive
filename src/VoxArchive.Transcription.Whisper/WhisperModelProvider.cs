using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// Whisperモデルのcatalogと物理配置規則を提供する
/// </summary>
public sealed class WhisperModelProvider : ITranscriptionModelProvider
{
    private readonly TranscriptionModelPackageInstaller _installer;
    private readonly IReadOnlyDictionary<string, TranscriptionModelPackageDefinition> _definitions;
    private readonly string _modelsRootDirectory;

    /// <summary>
    /// 共通installerと既定モデル保存先でProviderを初期化する
    /// </summary>
    public WhisperModelProvider(TranscriptionModelPackageInstaller installer)
        : this(installer, null)
    {
    }

    internal WhisperModelProvider(TranscriptionModelPackageInstaller installer, string? modelsRootDirectory)
    {
        _installer = installer;
        _modelsRootDirectory = string.IsNullOrWhiteSpace(modelsRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive", "models")
            : modelsRootDirectory;
        _definitions = WhisperModelCatalog.All.ToDictionary(x => x.ModelId.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => WhisperEngineIdentity.EngineId;

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
        => WhisperModelCatalog.All.Select(ToDescriptor).ToArray();

    /// <inheritdoc />
    public bool IsReady(TranscriptionModelId modelId)
        => Inspect(modelId, TranscriptionModelInspectionLevel.Size).State == TranscriptionModelPackageState.Installed;

    /// <inheritdoc />
    public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
    {
        var definition = Resolve(modelId);
        var state = _installer.Inspect(definition, GetInstallationDirectory(definition), level);
        return new TranscriptionModelInspection(state, level);
    }

    /// <inheritdoc />
    public async Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var definition = Resolve(modelId);
        var directory = await _installer.InstallAsync(
            definition,
            _modelsRootDirectory,
            force,
            progress,
            cancellationToken);
        return BuildInstallation(definition, directory);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TranscriptionModelPackageInstaller.Delete(Resolve(modelId), _modelsRootDirectory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
    {
        var definition = Resolve(modelId);
        if (!IsReady(modelId))
        {
            throw new InvalidOperationException($"Whisperモデル '{modelId}' は実行可能な状態ではありません。");
        }
        return BuildInstallation(definition, GetInstallationDirectory(definition));
    }

    private TranscriptionModelPackageDefinition Resolve(TranscriptionModelId modelId)
        => _definitions.TryGetValue(modelId.Value, out var definition)
            ? definition
            : throw new NotSupportedException($"未対応のWhisperモデルです: {modelId}");

    private string GetInstallationDirectory(TranscriptionModelPackageDefinition definition)
        => Path.Combine(_modelsRootDirectory, definition.EngineId.Value, definition.ModelId.Value);

    private static TranscriptionModelDescriptor ToDescriptor(TranscriptionModelPackageDefinition definition)
        => new(definition.ModelId, definition.DisplayName, definition.ArtifactVersion, definition.Revision, definition.License);

    private static TranscriptionModelInstallation BuildInstallation(
        TranscriptionModelPackageDefinition definition,
        string directory)
        => new(
            definition.EngineId,
            definition.ModelId,
            definition.Files.Select(x => Path.Combine(directory, x.DestinationName)).ToArray());
}
