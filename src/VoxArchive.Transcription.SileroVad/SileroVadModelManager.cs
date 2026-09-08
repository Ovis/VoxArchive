using VoxArchive.Transcription;

namespace VoxArchive.Transcription.SileroVad;

/// <summary>
/// Silero VADモデルの取得、load validation、削除、session内の利用可否cacheを管理する
/// </summary>
/// <remarks>
/// SileroはASR EngineではないためTranscriptionEngineRegistryへ登録せず、VAD project内の専用managerとして扱う。
/// モデル取得・削除・native loadを伴う状態確認時はTranscriptionModelUsageTrackerのglobal blockを利用し、
/// ASRモデル管理や文字起こしと同時実行しない。
/// </remarks>
public sealed class SileroVadModelManager : ISpeechRegionDetectorModelManager
{
    private static readonly Uri ModelSource = new(
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx");

    private readonly ManagedModelFileTransaction _transaction;
    private readonly TranscriptionModelUsageTracker _usageTracker;
    private readonly Action<string> _validateModelLoad;
    private readonly string _modelPath;
    private readonly object _gate = new();
    private bool? _cachedAvailability;
    private bool _isChecking;

    /// <summary>
    /// 既定モデル配置とSilero native load validationを利用するmanagerを生成する
    /// </summary>
    public SileroVadModelManager(
        ManagedModelFileTransaction transaction,
        TranscriptionModelUsageTracker usageTracker)
        : this(transaction, usageTracker, SileroVadModelPath.GetDefault(), SileroVadDetector.ValidateModelLoad)
    {
    }

    internal SileroVadModelManager(
        ManagedModelFileTransaction transaction,
        TranscriptionModelUsageTracker usageTracker,
        string modelPath,
        Action<string> validateModelLoad)
    {
        _transaction = transaction;
        _usageTracker = usageTracker;
        _modelPath = modelPath;
        _validateModelLoad = validateModelLoad;
    }

    /// <summary>現在のSileroモデル状態を取得する</summary>
    public SileroVadModelState GetState()
    {
        lock (_gate)
        {
            if (_isChecking) return SileroVadModelState.Checking;
            if (!File.Exists(_modelPath))
            {
                _cachedAvailability = null;
                return SileroVadModelState.Missing;
            }
            if (_cachedAvailability.HasValue)
            {
                return _cachedAvailability.Value
                    ? SileroVadModelState.Available
                    : SileroVadModelState.Unavailable;
            }
        }

        // cacheがない初回状態確認だけnative model loadが発生する。
        // 単なるFile.Exists確認とは異なり文字起こし中のnative利用と競合し得るため、明示再確認と同じglobal blockを通す。
        using var usageBlock = _usageTracker.BlockNewReservations("Silero VADモデル状態確認");
        return ValidateCurrentModel(force: false);
    }

    /// <summary>
    /// 現在配置されているモデルを強制的にnative loadし直して状態を返す
    /// </summary>
    public SileroVadModelState Recheck()
    {
        using var usageBlock = _usageTracker.BlockNewReservations("Silero VADモデル再確認");
        return ValidateCurrentModel(force: true);
    }

    /// <summary>
    /// Silero VADモデルを一時領域へ取得し、native load成功後だけ確定配置へ反映する
    /// </summary>
    public async Task InstallAsync(
        bool force,
        IProgress<ManagedModelTransactionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var usageBlock = _usageTracker.BlockNewReservations(force ? "Silero VADモデル再取得" : "Silero VADモデル取得");

        // Install自身がglobal blockを保持しているため、GetStateを経由すると同じUsageTrackerへ二重blockしてしまう。
        // ここでは同じnative validation本体を直接呼び、取得不要かだけを判定する。
        if (!force && ValidateCurrentModel(force: false) == SileroVadModelState.Available)
        {
            return;
        }

        SetChecking(true);
        try
        {
            InvalidateCache();
            var destinationDirectory = Path.GetDirectoryName(_modelPath)
                ?? throw new InvalidOperationException("Silero VADモデル配置先ディレクトリを解決できません。");
            await _transaction.DownloadValidateCommitAsync(
                [new ManagedModelDownloadFile(ModelSource, Path.GetFileName(_modelPath))],
                destinationDirectory,
                GetTemporaryRootDirectory(),
                staging =>
                {
                    _validateModelLoad(Path.Combine(staging, Path.GetFileName(_modelPath)));
                    return Task.CompletedTask;
                },
                progress,
                cancellationToken,
                committed =>
                {
                    // 正式配置側でもloadできることをbackup保持中に確認する。
                    // path固有の問題があればtransactionが旧モデルへrollbackするため、既存正常モデルを失わない。
                    _validateModelLoad(Path.Combine(committed, Path.GetFileName(_modelPath)));
                    return Task.CompletedTask;
                });

            lock (_gate) _cachedAvailability = true;
        }
        finally
        {
            SetChecking(false);
        }
    }

    /// <summary>
    /// Silero VADモデルを安全なrename経由で削除する
    /// </summary>
    public void Delete()
    {
        using var usageBlock = _usageTracker.BlockNewReservations("Silero VADモデル削除");
        var destinationDirectory = Path.GetDirectoryName(_modelPath)
            ?? throw new InvalidOperationException("Silero VADモデル配置先ディレクトリを解決できません。");
        _transaction.DeleteAtomically(destinationDirectory, GetTemporaryRootDirectory());
        InvalidateCache();
    }

    /// <summary>
    /// 前回異常終了などで残ったVoxArchive管理のモデル一時領域を掃除する
    /// </summary>
    public void CleanupTemporaryDirectories()
        => _transaction.CleanupOwnedTemporaryDirectories(GetTemporaryRootDirectory());

    SpeechRegionDetectorModelState ISpeechRegionDetectorModelManager.GetState()
        => ToCommonState(GetState());

    SpeechRegionDetectorModelState ISpeechRegionDetectorModelManager.Recheck()
        => ToCommonState(Recheck());

    private static SpeechRegionDetectorModelState ToCommonState(SileroVadModelState state)
        => state switch
        {
            SileroVadModelState.Missing => SpeechRegionDetectorModelState.Missing,
            SileroVadModelState.Available => SpeechRegionDetectorModelState.Available,
            SileroVadModelState.Unavailable => SpeechRegionDetectorModelState.Unavailable,
            SileroVadModelState.Checking => SpeechRegionDetectorModelState.Checking,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知のSilero VADモデル状態です。")
        };

    private SileroVadModelState ValidateCurrentModel(bool force)
    {
        lock (_gate)
        {
            if (!File.Exists(_modelPath))
            {
                _cachedAvailability = null;
                return SileroVadModelState.Missing;
            }
            if (!force && _cachedAvailability.HasValue)
            {
                return _cachedAvailability.Value
                    ? SileroVadModelState.Available
                    : SileroVadModelState.Unavailable;
            }
            _isChecking = true;
        }

        try
        {
            try
            {
                _validateModelLoad(_modelPath);
                lock (_gate) _cachedAvailability = true;
                return SileroVadModelState.Available;
            }
            catch
            {
                lock (_gate) _cachedAvailability = false;
                return SileroVadModelState.Unavailable;
            }
        }
        finally
        {
            SetChecking(false);
        }
    }

    private string GetTemporaryRootDirectory()
    {
        var modelDirectory = Path.GetDirectoryName(_modelPath)
            ?? throw new InvalidOperationException("Silero VADモデル配置先ディレクトリを解決できません。");
        var modelsRoot = Directory.GetParent(modelDirectory)?.FullName
            ?? throw new InvalidOperationException("Silero VADモデルルートを解決できません。");
        return Path.Combine(modelsRoot, ".model-ops");
    }

    private void InvalidateCache()
    {
        lock (_gate) _cachedAvailability = null;
    }

    private void SetChecking(bool value)
    {
        lock (_gate) _isChecking = value;
    }
}

/// <summary>
/// Silero VADモデルの利用状態を表す
/// </summary>
public enum SileroVadModelState
{
    /// <summary>モデルファイルが未取得</summary>
    Missing = 0,

    /// <summary>native loadまで成功し利用可能</summary>
    Available = 1,

    /// <summary>ファイルは存在するがnative loadに失敗</summary>
    Unavailable = 2,

    /// <summary>取得後検証または明示再確認を実行中</summary>
    Checking = 3
}
