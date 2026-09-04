using System.IO;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.Wpf;

/// <summary>
/// Whisperモデルを共通モデルパッケージ基盤へ接続するProviderを提供する
/// </summary>
/// <remarks>
/// 保存先は <c>%LOCALAPPDATA%\VoxArchive\models\whisper\&lt;modelId&gt;</c> に統一する。
/// 旧 <c>%LOCALAPPDATA%\VoxArchive\whisper\models</c> は互換対象とせず、検出・移行・削除も行わない。
/// </remarks>
public sealed class WhisperModelStore : ITranscriptionModelProvider
{
    private readonly TranscriptionModelPackageInstaller _installer;
    private readonly IReadOnlyDictionary<string, TranscriptionModelDefinition> _definitions;
    private readonly string _modelsRootDirectory;
    private readonly object _downloadStateLock = new();
    private readonly HashSet<TranscriptionModel> _downloadingModels = [];

    /// <summary>既定のモデル保存先とHTTPクライアントでWhisperモデルStoreを初期化する</summary>
    public WhisperModelStore()
        : this(new TranscriptionModelPackageInstaller(new HttpClient()), WhisperModelCatalog.All, null)
    {
    }

    /// <summary>DIから共通PackageInstallerを受け取りWhisperモデルStoreを初期化する</summary>
    public WhisperModelStore(TranscriptionModelPackageInstaller installer)
        : this(installer, WhisperModelCatalog.All, null)
    {
    }

    /// <summary>テスト等でモデル保存ルートを上書きしてWhisperモデルStoreを初期化する</summary>
    public WhisperModelStore(string modelsRootDirectory)
        : this(new TranscriptionModelPackageInstaller(new HttpClient()), WhisperModelCatalog.All, modelsRootDirectory)
    {
    }

    /// <summary>
    /// WhisperモデルStoreを固定モデル定義と保存ルートから初期化する
    /// </summary>
    internal WhisperModelStore(
        TranscriptionModelPackageInstaller installer,
        IEnumerable<TranscriptionModelDefinition> definitions,
        string? modelsRootDirectory)
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
            if (definition.EngineId != TranscriptionEngineId.Whisper)
            {
                throw new ArgumentException($"Whisper以外のモデル定義は登録できません: {definition.EngineId}", nameof(definitions));
            }

            if (!resolvedDefinitions.TryAdd(definition.ModelId.Value, definition))
            {
                throw new ArgumentException($"WhisperモデルIDが重複しています: {definition.ModelId}", nameof(definitions));
            }
        }

        _definitions = resolvedDefinitions;
    }

    /// <summary>
    /// モデルのダウンロード状態が変化したときに通知する
    /// </summary>
    /// <remarks>
    /// 旧SettingsWindowとの互換用イベントであり、共通モデル管理UIではTranscriptionModelManagerの状態通知を使用する。
    /// </remarks>
    public event EventHandler<WhisperModelDownloadStateChangedEventArgs>? DownloadStateChanged;

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => TranscriptionEngineId.Whisper;

    /// <summary>Whisperモデルを配置するEngineディレクトリを取得する</summary>
    public string ModelsDirectory => Path.Combine(_modelsRootDirectory, EngineId.Value);

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
    {
        return WhisperModelCatalog.All
            .Select(definition => new TranscriptionModelDescriptor(definition.ModelId, definition.DisplayName))
            .ToArray();
    }

    /// <summary>指定したWhisperモデルの物理パスを取得する</summary>
    public string GetModelPath(TranscriptionModel model)
    {
        var definition = ResolveDefinition(ToModelId(model));
        return Path.Combine(GetInstallationDirectory(definition), definition.Files[0].DestinationName);
    }

    /// <summary>指定したWhisperモデルが文字起こし実行可能なサイズで配置済みか確認する</summary>
    public bool IsInstalled(TranscriptionModel model)
    {
        return IsInstalled(ToModelId(model));
    }

    /// <summary>指定したWhisperモデルを現在ダウンロードしているか確認する</summary>
    public bool IsDownloading(TranscriptionModel model)
    {
        lock (_downloadStateLock)
        {
            return _downloadingModels.Contains(model);
        }
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

    /// <summary>指定したWhisperモデルを削除する</summary>
    public Task DeleteAsync(TranscriptionModel model, CancellationToken cancellationToken = default)
    {
        return DeleteAsync(ToModelId(model), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ParseModelId(modelId);
        if (IsDownloading(model))
        {
            throw new InvalidOperationException("ダウンロード中のモデルは削除できません。");
        }

        var definition = ResolveDefinition(modelId);
        TranscriptionModelPackageInstaller.Delete(definition, _modelsRootDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 指定したWhisperモデルを取得する
    /// </summary>
    public async Task<string> DownloadAsync(TranscriptionModel model, CancellationToken cancellationToken = default)
    {
        var installation = await InstallTrackedAsync(ToModelId(model), force: false, progress: null, cancellationToken);
        return installation.PrimaryFile;
    }

    /// <inheritdoc />
    public Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        CancellationToken cancellationToken = default)
    {
        return InstallTrackedAsync(modelId, force: false, progress: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TranscriptionModelInstallation> InstallManagedAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return InstallTrackedAsync(modelId, force, progress, cancellationToken);
    }

    /// <inheritdoc />
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
    {
        var definition = ResolveDefinition(modelId);
        var inspection = Inspect(modelId, TranscriptionModelInspectionLevel.Size);
        if (inspection.State != TranscriptionModelPackageState.Installed)
        {
            throw new InvalidOperationException($"Whisperモデル '{modelId}' は文字起こしに利用できる状態で配置されていません。");
        }

        return BuildInstallation(definition);
    }

    /// <summary>Whisperモデルに対応するファイル名を取得する</summary>
    public static string GetModelFileName(TranscriptionModel model)
    {
        return model switch
        {
            TranscriptionModel.Tiny => "ggml-tiny.bin",
            TranscriptionModel.Base => "ggml-base.bin",
            TranscriptionModel.Small => "ggml-small.bin",
            TranscriptionModel.Medium => "ggml-medium.bin",
            TranscriptionModel.LargeV3 => "ggml-large-v3.bin",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "未対応のWhisperモデルです。")
        };
    }

    private async Task<TranscriptionModelInstallation> InstallTrackedAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var model = ParseModelId(modelId);
        lock (_downloadStateLock)
        {
            if (!_downloadingModels.Add(model))
            {
                throw new InvalidOperationException("このモデルは現在ダウンロード中です。");
            }
        }

        DownloadStateChanged?.Invoke(this, new WhisperModelDownloadStateChangedEventArgs(model, true));
        try
        {
            var definition = ResolveDefinition(modelId);
            var directory = await _installer.InstallAsync(definition, _modelsRootDirectory, force, progress, cancellationToken);
            return BuildInstallation(definition, directory);
        }
        finally
        {
            lock (_downloadStateLock)
            {
                _downloadingModels.Remove(model);
            }

            DownloadStateChanged?.Invoke(this, new WhisperModelDownloadStateChangedEventArgs(model, false));
        }
    }

    private TranscriptionModelDefinition ResolveDefinition(TranscriptionModelId modelId)
    {
        if (_definitions.TryGetValue(modelId.Value, out var definition))
        {
            return definition;
        }

        throw new NotSupportedException($"Whisperモデル '{modelId}' はサポートされていません。");
    }

    private string GetInstallationDirectory(TranscriptionModelDefinition definition)
    {
        return Path.Combine(_modelsRootDirectory, definition.EngineId.Value, definition.ModelId.Value);
    }

    private TranscriptionModelInstallation BuildInstallation(TranscriptionModelDefinition definition)
    {
        return BuildInstallation(definition, GetInstallationDirectory(definition));
    }

    private static TranscriptionModelInstallation BuildInstallation(TranscriptionModelDefinition definition, string directory)
    {
        var files = definition.Files.Select(file => Path.Combine(directory, file.DestinationName)).ToArray();
        return new TranscriptionModelInstallation(definition.EngineId, definition.ModelId, files);
    }

    private static TranscriptionModelId ToModelId(TranscriptionModel model)
    {
        return new TranscriptionModelId(model switch
        {
            TranscriptionModel.Tiny => "tiny",
            TranscriptionModel.Base => "base",
            TranscriptionModel.Small => "small",
            TranscriptionModel.Medium => "medium",
            TranscriptionModel.LargeV3 => "large-v3",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "未対応のWhisperモデルです。")
        });
    }

    private static TranscriptionModel ParseModelId(TranscriptionModelId modelId)
    {
        return modelId.Value switch
        {
            "tiny" => TranscriptionModel.Tiny,
            "base" => TranscriptionModel.Base,
            "small" => TranscriptionModel.Small,
            "medium" => TranscriptionModel.Medium,
            "large-v3" => TranscriptionModel.LargeV3,
            _ => throw new NotSupportedException($"Whisperモデル '{modelId}' はサポートされていません。 ")
        };
    }
}

/// <summary>
/// Whisperモデルのダウンロード状態変更を表す
/// </summary>
/// <param name="Model">状態が変化したモデル</param>
/// <param name="IsDownloading">ダウンロード中になった場合はtrue、終了した場合はfalse</param>
public sealed record WhisperModelDownloadStateChangedEventArgs(TranscriptionModel Model, bool IsDownloading);
