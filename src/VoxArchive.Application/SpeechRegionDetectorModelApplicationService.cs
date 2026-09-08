using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// 共通発話検出モデル管理をPresentation向けDTOへ投影する
/// </summary>
/// <remarks>
/// ApplicationはSilero具象型を知らず、Transcription層の共通モデル管理契約だけに依存する。
/// これにより将来detector実装を差し替えてもWPFの操作契約を変更せずに済む。
/// モデル操作はApplication scopeで追跡し、SettingsWindowの寿命とアプリ終了処理を分離する。
/// </remarks>
public sealed class SpeechRegionDetectorModelApplicationService(
    ISpeechRegionDetectorModelManager modelManager) : ISpeechRegionDetectorModelApplicationService
{
    private readonly object _operationGate = new();
    private ActiveOperation? _activeOperation;

    /// <inheritdoc />
    public SpeechRegionDetectorModelStatusInfo Inspect()
        => ToStatus(modelManager.GetState());

    /// <inheritdoc />
    public Task<SpeechRegionDetectorModelStatusInfo> ReverifyAsync(CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation("モデル再確認", canCancel: false, cancellationToken);
        return RunReverifyAsync(operation);
    }

    /// <inheritdoc />
    public Task InstallAsync(
        bool force,
        IProgress<SpeechRegionDetectorModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation(force ? "モデル再取得" : "モデル取得", canCancel: true, cancellationToken);
        var adapter = progress is null
            ? null
            : new Progress<ManagedModelTransactionProgress>(x =>
            {
                if (x.IsValidating)
                {
                    // native validation自体は安全に中断できない。CanCancelはUI表示用にfalseへ切り替えるが、
                    // 終了要求ではTokenをcancelしてvalidation完了後のcommitを抑止する。
                    operation.CanCancel = false;
                }

                progress.Report(new SpeechRegionDetectorModelTransferInfo(
                    x.BytesReceived,
                    x.TotalBytes,
                    x.CurrentFileName,
                    x.IsValidating));
            });

        return RunInstallAsync(operation, force, adapter);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation("モデル削除", canCancel: false, cancellationToken);
        return RunDeleteAsync(operation);
    }

    /// <inheritdoc />
    public SpeechRegionDetectorModelOperationInfo? GetActiveOperation()
    {
        lock (_operationGate)
        {
            return _activeOperation is null
                ? null
                : new SpeechRegionDetectorModelOperationInfo(
                    _activeOperation.OperationName,
                    _activeOperation.CanCancel);
        }
    }

    /// <inheritdoc />
    public async Task CancelActiveOperationAndWaitAsync()
    {
        ActiveOperation? operation;
        lock (_operationGate)
        {
            operation = _activeOperation;
            if (operation is null)
            {
                return;
            }

            // validation中もTokenはcancelする。native側にはTokenを渡していないため実処理は完走し、
            // transactionがvalidation直後にcancelを検出して公式配置へのcommitを行わない。
            if (!operation.Cancellation.IsCancellationRequested)
            {
                operation.Cancellation.Cancel();
            }
        }

        await operation.Completion.Task;
    }

    private async Task<SpeechRegionDetectorModelStatusInfo> RunReverifyAsync(ActiveOperation operation)
    {
        try
        {
            return await Task.Run(() =>
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                return ToStatus(modelManager.Recheck());
            });
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private async Task RunInstallAsync(
        ActiveOperation operation,
        bool force,
        IProgress<ManagedModelTransactionProgress>? progress)
    {
        try
        {
            await modelManager.InstallAsync(force, progress, operation.Cancellation.Token);
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private async Task RunDeleteAsync(ActiveOperation operation)
    {
        try
        {
            await Task.Run(() =>
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                modelManager.Delete();
            });
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private ActiveOperation BeginOperation(
        string operationName,
        bool canCancel,
        CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = new ActiveOperation(operationName, canCancel, cancellation);
        lock (_operationGate)
        {
            if (_activeOperation is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException($"発話検出モデルの別操作を実行中です: {_activeOperation.OperationName}");
            }

            _activeOperation = operation;
        }

        return operation;
    }

    private void CompleteOperation(ActiveOperation operation)
    {
        lock (_operationGate)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
            }
        }

        operation.Cancellation.Dispose();
        operation.Completion.TrySetResult();
    }

    private static SpeechRegionDetectorModelStatusInfo ToStatus(SpeechRegionDetectorModelState state)
        => new(
            state.ToString(),
            state == SpeechRegionDetectorModelState.Available);

    private sealed class ActiveOperation(
        string operationName,
        bool canCancel,
        CancellationTokenSource cancellation)
    {
        public string OperationName { get; } = operationName;
        public bool CanCancel { get; set; } = canCancel;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
