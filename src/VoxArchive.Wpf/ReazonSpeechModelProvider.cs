using System.IO;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// ReazonSpeechの論理モデルを共通モデルパッケージ基盤へ接続するProviderを提供する
/// </summary>
/// <remarks>
/// ReazonSpeechはencoder・decoder・joiner・tokensなど複数ファイルで1モデルを構成するため、
/// 呼び出し側が個々の物理ファイルや配置先を意識しないよう、モデル定義の解決と配置処理をこのクラスへ閉じ込める。
/// </remarks>
public sealed class ReazonSpeechModelProvider : ITranscriptionModelProvider
{
    private readonly TranscriptionModelPackageInstaller _installer;
    private readonly IReadOnlyDictionary<string, TranscriptionModelDefinition> _definitions;
    private readonly string _modelsRootDirectory;

    /// <summary>
    /// ReazonSpeechモデルProviderを初期化する
    /// </summary>
    /// <param name="installer">複数ファイルモデルの取得・検証・アトミック配置を行うInstaller</param>
    /// <param name="definitions">このProviderが管理するReazonSpeechモデル定義</param>
    /// <param name="modelsRootDirectory">モデル保存ルート。省略時は <c>%LOCALAPPDATA%\VoxArchive\models</c> を使用する</param>
    public ReazonSpeechModelProvider(
        TranscriptionModelPackageInstaller installer,
        IEnumerable<TranscriptionModelDefinition> definitions,
        string? modelsRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(definitions);

        _installer = installer;
        _modelsRootDirectory = string.IsNullOrWhiteSpace(modelsRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive", "models")
            : modelsRootDirectory;

        var resolvedDefinitions = new Dictionary<string, TranscriptionModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (definition.EngineId != TranscriptionEngineId.ReazonSpeech)
            {
                throw new ArgumentException(
                    $"ReazonSpeech以外のモデル定義は登録できません: {definition.EngineId}",
                    nameof(definitions));
            }

            if (!resolvedDefinitions.TryAdd(definition.ModelId.Value, definition))
            {
                throw new ArgumentException(
                    $"ReazonSpeechモデルIDが重複しています: {definition.ModelId}",
                    nameof(definitions));
            }
        }

        _definitions = resolvedDefinitions;
    }

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => TranscriptionEngineId.ReazonSpeech;

    /// <summary>ReazonSpeechモデルを配置する共通モデルルートを取得する</summary>
    public string ModelsRootDirectory => _modelsRootDirectory;

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
    {
        return _definitions.Values
            .Select(definition => new TranscriptionModelDescriptor(definition.ModelId, definition.DisplayName))
            .ToArray();
    }

    /// <inheritdoc />
    public bool IsInstalled(TranscriptionModelId modelId)
    {
        return Inspect(modelId, TranscriptionModelInspectionLevel.Size).State == TranscriptionModelPackageState.Installed;
    }

    /// <inheritdoc />
    public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
    {
        var definition = ResolveDefinition(modelId);
        var state = _installer.Inspect(definition, GetInstallationDirectory(definition), level);
        return new TranscriptionModelInspection(state, level);
    }

    /// <inheritdoc />
    public Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        CancellationToken cancellationToken = default)
    {
        return InstallManagedAsync(modelId, force: false, progress: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TranscriptionModelInstallation> InstallManagedAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var definition = ResolveDefinition(modelId);
        var installationDirectory = await _installer.InstallAsync(
            definition,
            _modelsRootDirectory,
            force,
            progress,
            cancellationToken);
        return BuildInstallation(definition, installationDirectory);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = ResolveDefinition(modelId);
        TranscriptionModelPackageInstaller.Delete(definition, _modelsRootDirectory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
    {
        var definition = ResolveDefinition(modelId);
        var installationDirectory = GetInstallationDirectory(definition);
        if (Inspect(modelId, TranscriptionModelInspectionLevel.Size).State != TranscriptionModelPackageState.Installed)
        {
            throw new InvalidOperationException($"ReazonSpeechモデル '{modelId}' は文字起こしに利用できる状態で配置されていません。");
        }

        return BuildInstallation(definition, installationDirectory);
    }

    /// <summary>
    /// 指定した論理モデルに対応する固定モデル定義を取得する
    /// </summary>
    public TranscriptionModelDefinition GetDefinition(TranscriptionModelId modelId)
    {
        return ResolveDefinition(modelId);
    }

    private TranscriptionModelDefinition ResolveDefinition(TranscriptionModelId modelId)
    {
        if (_definitions.TryGetValue(modelId.Value, out var definition))
        {
            return definition;
        }

        throw new NotSupportedException($"ReazonSpeechモデル '{modelId}' はサポートされていません。");
    }

    private string GetInstallationDirectory(TranscriptionModelDefinition definition)
    {
        return Path.Combine(_modelsRootDirectory, definition.EngineId.Value, definition.ModelId.Value);
    }

    private static TranscriptionModelInstallation BuildInstallation(
        TranscriptionModelDefinition definition,
        string installationDirectory)
    {
        // Filesの順序はモデル定義側で意味を持つ可能性があるため、ファイルシステム列挙へ置き換えず定義順を維持する。
        var files = definition.Files
            .Select(file => Path.Combine(installationDirectory, file.DestinationName))
            .ToArray();
        return new TranscriptionModelInstallation(definition.EngineId, definition.ModelId, files);
    }
}
