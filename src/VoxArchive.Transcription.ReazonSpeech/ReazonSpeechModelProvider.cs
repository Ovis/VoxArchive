using SherpaOnnx;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Transcription.ReazonSpeech;

/// <summary>
/// ReazonSpeechモデルのprecision別物理配置、load validation、取得・削除を担当する
/// </summary>
public sealed class ReazonSpeechModelProvider : ITranscriptionModelProvider
{
    private const int ValidationSampleRate = 16_000;
    private const int ValidationFeatureDimension = 80;
    private readonly ManagedModelFileTransaction _transaction;
    private readonly IReadOnlyDictionary<string, ReazonSpeechManagedModelPackage> _packages;
    private readonly string _modelsRootDirectory;
    private readonly object _validationGate = new();
    private readonly Dictionary<string, bool> _validationCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 共通transactionと既定モデル保存先でProviderを初期化する
    /// </summary>
    public ReazonSpeechModelProvider(ManagedModelFileTransaction transaction)
        : this(transaction, null)
    {
    }

    internal ReazonSpeechModelProvider(ManagedModelFileTransaction transaction, string? modelsRootDirectory)
    {
        _transaction = transaction;
        _modelsRootDirectory = string.IsNullOrWhiteSpace(modelsRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive", "models")
            : modelsRootDirectory;
        _packages = ReazonSpeechModelCatalog.Packages.ToDictionary(x => x.PackageId.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => ReazonSpeechEngineIdentity.EngineId;

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
        =>
        [
            // 現行WPFは論理モデルIDを直接モデル管理APIへ渡すため、Phase 7でUIをprecision対応するまで
            // jaを既定hybrid packageへの互換aliasとして残す。Admissionはprecision別package IDを利用する。
            new(ReazonSpeechModelCatalog.JapaneseModelId, "日本語（k2-v2）", "k2-v2", ReazonSpeechModelCatalog.Revision, "Apache-2.0"),
            .. ReazonSpeechModelCatalog.Packages.Select(x => new TranscriptionModelDescriptor(
                x.PackageId,
                $"日本語（k2-v2 / {ToPrecisionLabel(x.Precision)}）",
                "k2-v2",
                ReazonSpeechModelCatalog.Revision,
                "Apache-2.0"))
        ];

    /// <inheritdoc />
    public bool IsReady(TranscriptionModelId modelId)
    {
        var package = ResolvePackage(modelId);
        lock (_validationGate)
        {
            if (_validationCache.TryGetValue(package.PackageId.Value, out var cached)) return cached;
        }

        var directory = GetInstallationDirectory(package);
        var ready = HasAllRequiredFiles(package, directory) && TryValidateLoad(package, directory);
        lock (_validationGate)
        {
            _validationCache[package.PackageId.Value] = ready;
        }
        return ready;
    }

    /// <inheritdoc />
    public TranscriptionModelInspection Inspect(TranscriptionModelId modelId, TranscriptionModelInspectionLevel level)
    {
        var package = ResolvePackage(modelId);
        var directory = GetInstallationDirectory(package);
        var existing = package.Files.Count(file => File.Exists(Path.Combine(directory, file.DestinationName)));
        var state = existing switch
        {
            0 => TranscriptionModelPackageState.Missing,
            _ when existing != package.Files.Count => TranscriptionModelPackageState.Incomplete,
            _ => TranscriptionModelPackageState.Installed
        };

        // ReazonSpeechではSHA-256を利用可能判定に使わない。既存APIの明示再確認経路だけは
        // Hash levelを「強制load validation」のトリガーとして扱い、Phase 7で表示文言も合わせて変更する。
        if (state == TranscriptionModelPackageState.Installed && level == TranscriptionModelInspectionLevel.Hash)
        {
            InvalidateValidation(package.PackageId);
            state = IsReady(package.PackageId)
                ? TranscriptionModelPackageState.Installed
                : TranscriptionModelPackageState.Corrupt;
        }

        return new TranscriptionModelInspection(state, level);
    }

    /// <inheritdoc />
    public async Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        bool force,
        IProgress<TranscriptionModelTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var package = ResolvePackage(modelId);
        if (!force && IsReady(package.PackageId))
        {
            return BuildInstallation(package, GetInstallationDirectory(package));
        }

        InvalidateValidation(package.PackageId);
        var adapter = progress is null
            ? null
            : new Progress<ManagedModelTransactionProgress>(x =>
                progress.Report(new TranscriptionModelTransferProgress(x.BytesReceived, x.TotalBytes ?? 0)));
        var directory = await _transaction.DownloadValidateCommitAsync(
            package.Files,
            GetInstallationDirectory(package),
            GetTemporaryRootDirectory(),
            staging =>
            {
                ValidateLoad(package, staging);
                return Task.CompletedTask;
            },
            adapter,
            cancellationToken);
        InvalidateValidation(package.PackageId);
        if (!IsReady(package.PackageId))
        {
            throw new InvalidDataException("ReazonSpeechモデルは配置後の再確認に失敗しました。");
        }
        return BuildInstallation(package, directory);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolvePackage(modelId);

        // 「モデルを削除」は現在選択中precisionだけでなく、残してある他precisionも含む
        // ReazonSpeech k2-v2のローカルセット全体を削除する仕様である。
        var engineDirectory = Path.Combine(_modelsRootDirectory, EngineId.Value);
        _transaction.DeleteSafely(engineDirectory, GetTemporaryRootDirectory());
        lock (_validationGate) _validationCache.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
    {
        var package = ResolvePackage(modelId);
        if (!IsReady(package.PackageId))
        {
            throw new InvalidOperationException($"ReazonSpeechモデル '{package.PackageId}' は実行可能な状態ではありません。");
        }
        return BuildInstallation(package, GetInstallationDirectory(package));
    }

    private ReazonSpeechManagedModelPackage ResolvePackage(TranscriptionModelId modelId)
    {
        // 旧UI/APIから渡る論理ID ja は既定profileのhybrid packageへ互換解決する。
        var physicalId = modelId == ReazonSpeechModelCatalog.JapaneseModelId
            ? ReazonSpeechModelCatalog.JapaneseInt8Fp32PackageId
            : modelId;
        return _packages.TryGetValue(physicalId.Value, out var package)
            ? package
            : throw new NotSupportedException($"未対応のReazonSpeechモデルpackageです: {modelId}");
    }

    private string GetInstallationDirectory(ReazonSpeechManagedModelPackage package)
        => Path.Combine(_modelsRootDirectory, EngineId.Value, package.PackageId.Value);

    private string GetTemporaryRootDirectory()
        => Path.Combine(_modelsRootDirectory, ".model-ops");

    private static bool HasAllRequiredFiles(ReazonSpeechManagedModelPackage package, string directory)
        => package.Files.All(file => File.Exists(Path.Combine(directory, file.DestinationName)));

    private bool TryValidateLoad(ReazonSpeechManagedModelPackage package, string directory)
    {
        try
        {
            ValidateLoad(package, directory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateLoad(ReazonSpeechManagedModelPackage package, string directory)
    {
        var required = ReazonSpeechModelRequirementResolver.GetRequiredFileNames(package.Precision);
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = ValidationSampleRate;
        config.FeatConfig.FeatureDim = ValidationFeatureDimension;
        config.ModelConfig.Transducer.Encoder = Path.Combine(directory, required.Encoder);
        config.ModelConfig.Transducer.Decoder = Path.Combine(directory, required.Decoder);
        config.ModelConfig.Transducer.Joiner = Path.Combine(directory, required.Joiner);
        config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 1;
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;

        // availabilityは利用者のdecoding/thread設定と切り離し、固定最小構成でnative初期化できることだけを確認する。
        using var recognizer = new OfflineRecognizer(config);
    }

    private void InvalidateValidation(TranscriptionModelId packageId)
    {
        lock (_validationGate) _validationCache.Remove(packageId.Value);
    }

    private static TranscriptionModelInstallation BuildInstallation(
        ReazonSpeechManagedModelPackage package,
        string directory)
        => new(
            ReazonSpeechEngineIdentity.EngineId,
            package.PackageId,
            package.Files.Select(x => Path.Combine(directory, x.DestinationName)).ToArray());

    private static string ToPrecisionLabel(ReazonSpeechPrecision precision)
        => precision switch
        {
            ReazonSpeechPrecision.Fp32 => "FP32",
            ReazonSpeechPrecision.Int8 => "INT8",
            ReazonSpeechPrecision.Int8Fp32 => "INT8-FP32",
            _ => precision.ToString()
        };
}
